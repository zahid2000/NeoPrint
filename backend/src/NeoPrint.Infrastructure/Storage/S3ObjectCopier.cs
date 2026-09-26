using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Copies one stored object between providers, and proves the copy.
/// </summary>
/// <remarks>
/// Shared by the migration and the mirror, so both make the same guarantee:
/// the ciphertext moves as it is - nothing is decrypted - and a copy only
/// counts once it has been read back from the target and its SHA-256
/// matched the source's.
/// </remarks>
public static class S3ObjectCopier
{
    /// <summary>
    /// Copies one object and proves the copy by reading it back.
    /// </summary>
    public static async Task<long> CopyAsync(
        IAmazonS3 source,
        string sourceBucket,
        IAmazonS3 target,
        string targetBucket,
        string key,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-sync-{Guid.NewGuid():N}");

        try
        {
            byte[] sourceHash;
            Dictionary<string, string> metadata;

            using (var response = await source
                       .GetObjectAsync(new GetObjectRequest { BucketName = sourceBucket, Key = key }, cancellationToken)
                       .ConfigureAwait(false))
            {
                metadata = response.Metadata.Keys.ToDictionary(k => k, k => response.Metadata[k]);

                await using var file = File.Create(scratch);
                sourceHash = await HashWhileCopyingAsync(response.ResponseStream, file, cancellationToken)
                    .ConfigureAwait(false);
            }

            var length = new FileInfo(scratch).Length;

            await using (var body = File.OpenRead(scratch))
            {
                var put = new PutObjectRequest
                {
                    BucketName = targetBucket,
                    Key = key,
                    InputStream = body,
                    ContentType = "application/octet-stream",
                    AutoCloseStream = false,
                };

                // The operator-facing labels the application wrote travel with
                // the object; they are not authenticated and nothing reads
                // them as a decision.
                foreach (var (name, value) in metadata)
                {
                    put.Metadata.Add(name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                        ? name["x-amz-meta-".Length..]
                        : name, value);
                }

                await target.PutObjectAsync(put, cancellationToken).ConfigureAwait(false);
            }

            // Read it back. A copy that has only been written has not been
            // proven: a gateway that truncated or rewrote the body would pass
            // a length check on the request side.
            using var check = await target
                .GetObjectAsync(new GetObjectRequest { BucketName = targetBucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            var targetHash = await HashWhileCopyingAsync(check.ResponseStream, Stream.Null, cancellationToken)
                .ConfigureAwait(false);

            if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
            {
                throw new InvalidOperationException($"The copy of '{key}' does not match the source.");
            }

            return length;
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
                // Cleaned up by the operating system.
            }
        }
    }

    private static async Task<byte[]> HashWhileCopyingAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return hash.GetHashAndReset();
    }

}
