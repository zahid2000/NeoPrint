using System.Diagnostics;
using System.Text;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging;

using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>What a connection test found.</summary>
/// <param name="Succeeded">Whether every bucket passed.</param>
/// <param name="Message">One sentence for the panel. Never a credential.</param>
/// <param name="Buckets">Per-bucket result, so a single bad bucket is visible.</param>
/// <param name="Elapsed">How long the round trip took.</param>
public sealed record StorageTestResult(
    bool Succeeded,
    string Message,
    IReadOnlyList<BucketTestResult> Buckets,
    TimeSpan Elapsed);

/// <param name="Bucket">The bucket name as configured.</param>
/// <param name="Succeeded">Whether put, get and delete all worked.</param>
/// <param name="Problem">What went wrong, or null.</param>
public sealed record BucketTestResult(string Bucket, bool Succeeded, string? Problem);

/// <summary>
/// Proves a storage provider actually works before it is given a role.
/// </summary>
/// <remarks>
/// <para>
/// Listing buckets is not enough: credentials that can list are not
/// necessarily credentials that can write, and a bucket that exists is not
/// necessarily one this account may put into. The test therefore writes a
/// small object, reads it back, compares the bytes and deletes it - which is
/// exactly what the application will do (plan section 2.10).
/// </para>
/// <para>
/// The test object is written with a name that says what it is and is deleted
/// on the way out, including when the read fails.
/// </para>
/// </remarks>
public sealed class StorageConnectionTester
{
    private readonly S3ClientFactory _clients;
    private readonly ILogger<StorageConnectionTester> _logger;

    public StorageConnectionTester(S3ClientFactory clients, ILogger<StorageConnectionTester> logger)
    {
        _clients = clients;
        _logger = logger;
    }

    /// <summary>Tests a provider that is already saved.</summary>
    public Task<StorageTestResult> TestAsync(
        StorageProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return RunAsync(_clients.For(provider), provider.Buckets, cancellationToken);
    }

    /// <summary>
    /// Tests credentials that have not been saved yet, for the add-provider
    /// form.
    /// </summary>
    public async Task<StorageTestResult> TestAsync(
        string endpoint,
        string region,
        bool useSsl,
        string accessKey,
        string secretKey,
        StorageBuckets buckets,
        CancellationToken cancellationToken = default)
    {
        using var client = S3ClientFactory.ForUnsaved(endpoint, region, useSsl, accessKey, secretKey);

        return await RunAsync(client, buckets, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StorageTestResult> RunAsync(
        IAmazonS3 client, StorageBuckets buckets, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<BucketTestResult>();

        foreach (var bucket in buckets.All())
        {
            if (string.IsNullOrWhiteSpace(bucket))
            {
                results.Add(new BucketTestResult(bucket, false, "Bucket adı boşdur."));
                continue;
            }

            results.Add(await TestBucketAsync(client, bucket, cancellationToken).ConfigureAwait(false));
        }

        stopwatch.Stop();

        var failed = results.Where(r => !r.Succeeded).ToList();

        var message = failed.Count == 0
            ? $"Bağlantı uğurludur: {results.Count} bucket-in hamısında yazma, oxuma və silmə işləyir."
            : $"{failed.Count} bucket-də problem var: {string.Join("; ", failed.Select(f => $"{f.Bucket} — {f.Problem}"))}";

        return new StorageTestResult(failed.Count == 0, message, results, stopwatch.Elapsed);
    }

    private async Task<BucketTestResult> TestBucketAsync(
        IAmazonS3 client, string bucket, CancellationToken cancellationToken)
    {
        var key = $"neoprint-connection-test/{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes(
            $"NeoPrint connection test {DateTime.UtcNow:O}. Safe to delete.");

        try
        {
            await client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = bucket,
                        Key = key,
                        InputStream = new MemoryStream(payload),
                        ContentType = "text/plain",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            using var response = await client
                .GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            using var readBack = new MemoryStream();
            await response.ResponseStream.CopyToAsync(readBack, cancellationToken).ConfigureAwait(false);

            if (!readBack.ToArray().AsSpan().SequenceEqual(payload))
            {
                // Rare, but it has to be caught here rather than on a user's
                // file: some gateways rewrite bodies.
                return new BucketTestResult(
                    bucket, false, "Oxunan məzmun yazılanla eyni deyil.");
            }

            return new BucketTestResult(bucket, true, null);
        }
        catch (AmazonS3Exception exception)
        {
            _logger.LogWarning(
                exception, "The connection test failed for bucket {Bucket}.", bucket);

            // The S3 error code, not the full exception: the message can carry
            // the endpoint and request details, and this text reaches the panel.
            return new BucketTestResult(
                bucket, false, Describe(exception));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The connection test failed for bucket {Bucket}.", bucket);

            return new BucketTestResult(bucket, false, "Provayderə qoşulmaq mümkün olmadı.");
        }
        finally
        {
            await TryDeleteAsync(client, bucket, key, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Describe(AmazonS3Exception exception) => exception.ErrorCode switch
    {
        "NoSuchBucket" => "Bucket mövcud deyil.",
        "AccessDenied" => "Açarların bu bucket-ə icazəsi yoxdur.",
        "InvalidAccessKeyId" => "Access key tanınmır.",
        "SignatureDoesNotMatch" => "Secret key düzgün deyil.",
        _ => $"Provayder xəta qaytardı ({exception.ErrorCode ?? "naməlum"}).",
    };

    private static async Task TryDeleteAsync(
        IAmazonS3 client, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception)
        {
            // The test object was never written, or is already gone. Either
            // way this must not turn a passing test into a failing one.
        }
    }
}
