using System.Globalization;
using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Crypto;
using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Object storage on any S3-compatible provider, with every object encrypted
/// before it leaves this process (plan sections 2.10, 2.13).
/// </summary>
/// <remarks>
/// <para>
/// Writes go to the current primary; reads go to whichever provider the
/// object was written to. That is the whole reason <see cref="StoredObject"/>
/// carries a provider id: changing the primary must not make yesterday's
/// files unreadable.
/// </para>
/// <para>
/// The S3 API needs a length before it will accept a body, and the ciphertext
/// is longer than the plaintext by a header and a frame per chunk. Rather than
/// guess, the encryptor writes to a temporary file first, which also gives the
/// hash of exactly what is uploaded. The file is deleted on the way out,
/// including when the upload fails.
/// </para>
/// </remarks>
public sealed class S3FileStorage : IFileStorage
{
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;
    private readonly EnvelopeEncryptor _encryptor;
    private readonly IKeyEncryptionKeyProvider _keys;
    private readonly ILogger<S3FileStorage> _logger;

    /// <summary>Publishes ObjectStored for mirror mode; null where nothing mirrors (tests).</summary>
    private readonly IEventPublisher? _events;

    public S3FileStorage(
        StorageProviderRegistry registry,
        S3ClientFactory clients,
        EnvelopeEncryptor encryptor,
        IKeyEncryptionKeyProvider keys,
        ILogger<S3FileStorage> logger,
        IEventPublisher? events = null)
    {
        _registry = registry;
        _clients = clients;
        _encryptor = encryptor;
        _keys = keys;
        _logger = logger;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<StoredObject> WriteAsync(
        StorageBucket bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        var provider = await _registry.GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-upload-{Guid.NewGuid():N}");

        try
        {
            EncryptedObjectHeader header;
            string hash;
            long encryptedLength;

            await using (var buffer = File.Create(scratch))
            {
                header = await _encryptor.EncryptAsync(content, buffer, cancellationToken)
                    .ConfigureAwait(false);

                await buffer.FlushAsync(cancellationToken).ConfigureAwait(false);
                encryptedLength = buffer.Length;
            }

            // Over the ciphertext, not the plaintext: a sync verifies a copy
            // without decrypting it, and the store holds exactly these bytes.
            await using (var toHash = File.OpenRead(scratch))
            {
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(toHash, cancellationToken).ConfigureAwait(false));
            }

            await using (var body = File.OpenRead(scratch))
            {
                var request = new PutObjectRequest
                {
                    BucketName = BucketName(provider, bucket),
                    Key = key,
                    InputStream = body,

                    // Always this, never the caller's type: what is uploaded is
                    // ciphertext, and labelling it application/pdf would invite
                    // something to try to open it.
                    ContentType = "application/octet-stream",
                    AutoCloseStream = false,
                };

                // What the object actually is, for an operator looking at the
                // bucket. Metadata is not authenticated, so nothing reads it
                // back as a decision.
                request.Metadata.Add("neoprint-content-type", contentType);
                request.Metadata.Add("neoprint-kek", header.KekId);
                request.Metadata.Add(
                    "neoprint-plaintext-length",
                    header.PlaintextLength.ToString(CultureInfo.InvariantCulture));

                await _clients.For(provider).PutObjectAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            await DualWriteAsync(provider, bucket, key, scratch, header.KekId, cancellationToken)
                .ConfigureAwait(false);

            await AnnounceForMirrorAsync(provider, bucket, key, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Stored object {Key} in {Bucket} on provider {ProviderId} ({Bytes} bytes encrypted).",
                key,
                bucket,
                provider.Id,
                encryptedLength);

            return new StoredObject(
                provider.Id,
                key,
                hash,
                encryptedLength,
                header.PlaintextLength,
                header.KekId);
        }
        finally
        {
            Delete(scratch);
        }
    }

    /// <summary>
    /// Writes the same ciphertext to a migration's target while one is active.
    /// </summary>
    /// <remarks>
    /// Best effort by design. The user's upload has already succeeded on the
    /// primary, which stays the copy every file points at until the migration
    /// is verified; failing the upload because the target hiccuped would turn
    /// a background task's problem into the user's. What does not land here
    /// is found and copied by the migration's verification pass before any
    /// reference is switched (StorageMigrationWorker).
    /// </remarks>
    private async Task DualWriteAsync(
        StorageProvider primary,
        StorageBucket bucket,
        string key,
        string scratch,
        string kekId,
        CancellationToken cancellationToken)
    {
        StorageProvider? target;

        try
        {
            target = await _registry.GetMigrationTargetAsync(primary.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not look up an active migration; {Key} was written to the primary only.", key);
            return;
        }

        if (target is null)
        {
            return;
        }

        try
        {
            await using var body = File.OpenRead(scratch);

            var request = new PutObjectRequest
            {
                BucketName = BucketName(target, bucket),
                Key = key,
                InputStream = body,
                ContentType = "application/octet-stream",
                AutoCloseStream = false,
            };

            request.Metadata.Add("neoprint-kek", kekId);

            await _clients.For(target).PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Dual-write of {Key} to migration target {TargetId} failed; verification will copy it.",
                key,
                target.Id);
        }
    }

    /// <inheritdoc />
    public async Task ReadAsync(
        StoredObject stored,
        StorageBucket bucket,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(destination);

        var provider = await _registry.GetByIdAsync(stored.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        using var response = await GetAsync(provider, bucket, stored, cancellationToken)
            .ConfigureAwait(false);

        await _encryptor.DecryptAsync(response.ResponseStream, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // Decrypted into a temporary file rather than a MemoryStream: a 50 MB
        // file per concurrent download would be held in memory otherwise, and
        // the caller needs a seekable stream for range requests anyway.
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-read-{Guid.NewGuid():N}");

        var plaintext = new FileStream(
            scratch,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            // Deleted as soon as the caller disposes the stream, including
            // when the response is abandoned half way.
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            await ReadAsync(stored, bucket, plaintext, cancellationToken).ConfigureAwait(false);

            plaintext.Position = 0;
            return plaintext;
        }
        catch
        {
            await plaintext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);

        // Every provider, not just the one recorded: a mirror or a migration
        // may have left a copy elsewhere, and a file the user deleted must not
        // survive on it (plan section 2.10).
        var providers = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<string>();

        foreach (var provider in providers)
        {
            try
            {
                await _clients.For(provider).DeleteObjectAsync(
                        new DeleteObjectRequest
                        {
                            BucketName = BucketName(provider, bucket),
                            Key = stored.Key,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AmazonS3Exception exception) when (IsMissing(exception))
            {
                // Not an error: the object was never on this provider.
            }
            catch (Exception exception)
            {
                // One unreachable provider must not stop the others, or a
                // deletion request would leave copies on the providers that
                // were fine.
                _logger.LogError(
                    exception,
                    "Deleting {Key} from provider {ProviderId} failed.",
                    stored.Key,
                    provider.Id);

                failures.Add(provider.Name);
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"'{stored.Key}' could not be deleted from: {string.Join(", ", failures)}. "
                + "The object may still exist there.");
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var provider = await _registry.GetByIdAsync(stored.ProviderId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _clients.For(provider).GetObjectMetadataAsync(
                    new GetObjectMetadataRequest
                    {
                        BucketName = BucketName(provider, bucket),
                        Key = stored.Key,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            return false;
        }
    }

    private async Task<GetObjectResponse> GetAsync(
        StorageProvider provider,
        StorageBucket bucket,
        StoredObject stored,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetFromAsync(provider, bucket, stored, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            // Mirror mode's promise (plan section 2.10): the provider the
            // object was written to is not answering, and the secondary holds
            // a copy. Not on "not found" - that is an answer, and the
            // secondary is a copy of the primary, not a second opinion.
            var secondary = await _registry.GetSecondaryAsync(cancellationToken).ConfigureAwait(false);

            if (secondary is null || secondary.Id == provider.Id)
            {
                throw;
            }

            _logger.LogCritical(
                exception,
                "Storage provider {ProviderId} is not answering; {Key} is served from the secondary {SecondaryId}.",
                provider.Id,
                stored.Key,
                secondary.Id);

            return await GetFromAsync(secondary, bucket, stored, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GetObjectResponse> GetFromAsync(
        StorageProvider provider,
        StorageBucket bucket,
        StoredObject stored,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _clients.For(provider).GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = BucketName(provider, bucket),
                        Key = stored.Key,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (IsMissing(exception))
        {
            throw new StorageObjectNotFoundException(stored.Key, provider.Id);
        }
    }

    /// <summary>
    /// Whether a failure means the provider is down, as opposed to having
    /// answered.
    /// </summary>
    private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            StorageObjectNotFoundException => false,
            OperationCanceledException when cancellationToken.IsCancellationRequested => false,
            AmazonS3Exception s3 => (int)s3.StatusCode >= 500 || s3.StatusCode == 0,
            HttpRequestException or System.Net.Sockets.SocketException or TimeoutException => true,
            OperationCanceledException => true,
            Amazon.Runtime.AmazonServiceException => true,
            _ => exception.InnerException is not null && IsUnavailable(exception.InnerException, cancellationToken),
        };

    /// <summary>
    /// Tells the mirror about a write, when there is a secondary to mirror to.
    /// </summary>
    /// <remarks>
    /// After the write, and never failing it: the upload is complete on the
    /// primary, and a copy that is late or lost is what the reconciliation
    /// pass exists for.
    /// </remarks>
    private async Task AnnounceForMirrorAsync(
        StorageProvider provider, StorageBucket bucket, string key, CancellationToken cancellationToken)
    {
        if (_events is null)
        {
            return;
        }

        try
        {
            var secondary = await _registry.GetSecondaryAsync(cancellationToken).ConfigureAwait(false);

            if (secondary is null || secondary.Id == provider.Id)
            {
                return;
            }

            await _events.PublishAsync(
                    new Contracts.ObjectStored(provider.Id, bucket.ToString(), key), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not announce {Key} for mirroring; reconciliation will copy it.", key);
        }
    }

    /// <summary>The bucket this kind of object lives in on this provider.</summary>
    internal static string BucketName(StorageProvider provider, StorageBucket bucket) => bucket switch
    {
        StorageBucket.Files => provider.Buckets.Files,
        StorageBucket.Receipts => provider.Buckets.Receipts,
        StorageBucket.Temp => provider.Buckets.Temp,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, "Unknown bucket."),
    };

    /// <summary>
    /// Whether the store is saying "no such object" rather than failing.
    /// </summary>
    /// <remarks>
    /// The code differs between operations and between products: S3 and MinIO
    /// answer NoSuchKey to a GET and a bare 404 to a HEAD.
    /// </remarks>
    private static bool IsMissing(AmazonS3Exception exception)
        => exception.StatusCode == System.Net.HttpStatusCode.NotFound
           || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
           || string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temporary file is cleaned up by the operating system;
            // failing the request over it would be worse.
        }
    }
}
