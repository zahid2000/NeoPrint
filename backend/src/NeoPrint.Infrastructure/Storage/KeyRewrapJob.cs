using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging;

using NeoPrint.Crypto;
using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>What one rewrap pass did.</summary>
/// <param name="Examined">Objects looked at.</param>
/// <param name="Rewrapped">Objects whose data key now uses the active key.</param>
/// <param name="AlreadyCurrent">Objects that were already on the active key.</param>
/// <param name="Failed">Objects that could not be rewrapped.</param>
public sealed record RewrapSummary(int Examined, int Rewrapped, int AlreadyCurrent, int Failed)
{
    public bool Completed => Failed == 0;
}

/// <summary>
/// Moves stored objects onto the current key encryption key after a rotation
/// (plan section 2.13).
/// </summary>
/// <remarks>
/// <para>
/// Rotating a KEK does not re-encrypt anything: each object has its own data
/// key, and only that key is wrapped with the KEK. Rewrapping therefore reads
/// the header, unwraps the data key with the old KEK, wraps it with the new
/// one and copies the ciphertext through untouched - so the cost does not
/// depend on the size of the object.
/// </para>
/// <para>
/// Objects that were never rewrapped stay readable as long as the old key is
/// still in the ring, which is why <c>NEOPRINT_KEK__{old}</c> must not be
/// deleted until a pass has completed. The job is what makes deleting it safe;
/// it is started by an administrator rather than on a schedule, because that
/// is when it is needed and it touches every object in the store.
/// </para>
/// <para>
/// Idempotent and restart-safe by construction: an object already on the
/// active key is skipped, so a second run after a failure re-does only what
/// is left.
/// </para>
/// </remarks>
public sealed class KeyRewrapJob
{
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;
    private readonly EnvelopeEncryptor _encryptor;
    private readonly IKeyEncryptionKeyProvider _keys;
    private readonly ILogger<KeyRewrapJob> _logger;

    public KeyRewrapJob(
        StorageProviderRegistry registry,
        S3ClientFactory clients,
        EnvelopeEncryptor encryptor,
        IKeyEncryptionKeyProvider keys,
        ILogger<KeyRewrapJob> logger)
    {
        _registry = registry;
        _clients = clients;
        _encryptor = encryptor;
        _keys = keys;
        _logger = logger;
    }

    /// <summary>
    /// Rewraps every object in one bucket of one provider.
    /// </summary>
    public async Task<RewrapSummary> RunAsync(
        Guid providerId,
        Application.Abstractions.StorageBucket bucket,
        CancellationToken cancellationToken = default)
    {
        var provider = await _registry.GetByIdAsync(providerId, cancellationToken).ConfigureAwait(false);
        var bucketName = S3FileStorage.BucketName(provider, bucket);
        var client = _clients.For(provider);
        var activeKeyId = _keys.Current.Id;

        var examined = 0;
        var rewrapped = 0;
        var alreadyCurrent = 0;
        var failed = 0;

        string? continuationToken = null;

        do
        {
            var listing = await client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucketName,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in listing.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;

                try
                {
                    if (await RewrapOneAsync(client, bucketName, entry.Key, activeKeyId, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        rewrapped++;
                    }
                    else
                    {
                        alreadyCurrent++;
                    }
                }
                catch (Exception exception)
                {
                    // One bad object must not stop the pass: the point of the
                    // job is to let the old key be retired, and stopping at
                    // the first failure would leave everything after it on the
                    // old key with no record of how far it got.
                    _logger.LogError(
                        exception,
                        "Rewrapping {Key} in {Bucket} failed; the object stays on its current key.",
                        entry.Key,
                        bucketName);

                    failed++;
                }
            }

            continuationToken = listing.IsTruncated == true ? listing.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        _logger.LogInformation(
            "Rewrap of {Bucket} on provider {ProviderId} finished: {Examined} examined, "
            + "{Rewrapped} rewrapped onto '{KeyId}', {AlreadyCurrent} already current, {Failed} failed.",
            bucketName,
            providerId,
            examined,
            rewrapped,
            activeKeyId,
            alreadyCurrent,
            failed);

        return new RewrapSummary(examined, rewrapped, alreadyCurrent, failed);
    }

    /// <returns>True when the object was rewrapped, false when it was already current.</returns>
    private async Task<bool> RewrapOneAsync(
        IAmazonS3 client,
        string bucketName,
        string key,
        string activeKeyId,
        CancellationToken cancellationToken)
    {
        // Checked from the header rather than the object metadata: metadata is
        // not authenticated and could be stale or wrong, and getting this
        // wrong means retiring a key that objects still depend on.
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-rewrap-{Guid.NewGuid():N}");

        try
        {
            EncryptedObjectHeader? rewrappedHeader;

            using (var response = await client
                       .GetObjectAsync(new GetObjectRequest { BucketName = bucketName, Key = key }, cancellationToken)
                       .ConfigureAwait(false))
            await using (var destination = File.Create(scratch))
            {
                rewrappedHeader = await _encryptor
                    .RewrapAsync(response.ResponseStream, destination, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (rewrappedHeader is null)
            {
                return false;
            }

            await using (var body = File.OpenRead(scratch))
            {
                var request = new PutObjectRequest
                {
                    BucketName = bucketName,
                    Key = key,
                    InputStream = body,
                    ContentType = "application/octet-stream",
                    AutoCloseStream = false,
                };

                request.Metadata.Add("neoprint-kek", activeKeyId);

                await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(scratch))
                {
                    File.Delete(scratch);
                }
            }
            catch (IOException)
            {
                // Cleaned up by the operating system; not worth failing over.
            }
        }
    }
}
