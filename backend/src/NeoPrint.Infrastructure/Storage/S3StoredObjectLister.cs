using System.Runtime.CompilerServices;

using Amazon.S3.Model;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Operations;
using NeoPrint.Domain.Storage;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Lists a bucket on every provider in use - the primary, then a secondary or
/// mirror - for the file cleanup (T-215). A key held by both is listed once:
/// its deletion removes it from every provider anyway.
/// </summary>
public sealed class S3StoredObjectLister : IStoredObjectLister
{
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;

    public S3StoredObjectLister(StorageProviderRegistry registry, S3ClientFactory clients)
    {
        _registry = registry;
        _clients = clients;
    }

    public async IAsyncEnumerable<ListedObject> ListAsync(
        StorageBucket bucket, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var providers = (await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(provider => provider.Role != StorageProviderRole.Inactive)
            .OrderBy(provider => provider.Role == StorageProviderRole.Primary ? 0 : 1);

        foreach (var provider in providers)
        {
            var client = _clients.For(provider);
            var name = S3FileStorage.BucketName(provider, bucket);
            string? token = null;

            do
            {
                var listing = await client
                    .ListObjectsV2Async(new ListObjectsV2Request { BucketName = name, Prefix = prefix, ContinuationToken = token }, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var entry in listing.S3Objects ?? [])
                {
                    if (seen.Add(entry.Key))
                    {
                        var modified = entry.LastModified ?? DateTime.UtcNow;

                        yield return new ListedObject(
                            provider.Id,
                            entry.Key,
                            entry.Size ?? 0,
                            modified.Kind == DateTimeKind.Utc ? modified : modified.ToUniversalTime());
                    }
                }

                token = listing.IsTruncated == true ? listing.NextContinuationToken : null;
            }
            while (token is not null);
        }
    }
}
