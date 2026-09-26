using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Empties the source of a finished migration, once an administrator says so
/// (plan section 2.10: "Mənbədəki obyektlər <c>Storage.SourceRetentionDays</c>
/// sonra admin təsdiqi ilə silinir").
/// </summary>
/// <remarks>
/// <para>
/// Deleting is the one storage operation that cannot be undone, so an object
/// is deleted from the source only when all of these hold:
/// </para>
/// <list type="bullet">
/// <item>the source provider has no role any more - neither primary nor a
/// mirror's secondary, both of which are still in use;</item>
/// <item>no file record still points at the source for that key;</item>
/// <item>the target holds an object under the same key, of the same size.</item>
/// </list>
/// <para>
/// Anything that fails a check is kept and counted, not deleted. Re-running is
/// safe: what was deleted is gone from the listing, what was kept is checked
/// again. The job stops between objects when cancelled.
/// </para>
/// </remarks>
public sealed class StorageSourcePurge
{
    private readonly MongoContext _context;
    private readonly S3ClientFactory _clients;
    private readonly IClock _clock;
    private readonly ILogger<StorageSourcePurge> _logger;

    public StorageSourcePurge(
        MongoContext context, S3ClientFactory clients, IClock clock, ILogger<StorageSourcePurge> logger)
    {
        _context = context;
        _clients = clients;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Progress is saved this often, so a restart does not recount from zero.</summary>
    private const int SaveEvery = 100;

    /// <returns>Objects deleted and kept by this run.</returns>
    public async Task<(long Deleted, long Kept)> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.StorageSyncJobs.Find(j => j.Id == jobId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (job is null || job.SourcePurgeRequestedAt is null || job.SourcePurgedAt is not null)
        {
            return (0, 0);
        }

        var source = await LoadProviderAsync(job.SourceId, cancellationToken).ConfigureAwait(false);
        var target = await LoadProviderAsync(job.TargetId, cancellationToken).ConfigureAwait(false);

        if (source is null || target is null)
        {
            _logger.LogWarning("Source purge for sync job {JobId} skipped: a provider no longer exists.", jobId);
            return (0, 0);
        }

        if (source.Role != StorageProviderRole.Inactive)
        {
            // Checked when the purge was requested, and again here: the role
            // could have changed since, and a provider in use is never emptied.
            _logger.LogWarning(
                "Source purge for sync job {JobId} skipped: provider {Provider} is {Role} again.",
                jobId,
                source.Name,
                source.Role);
            return (0, 0);
        }

        var stillReferenced = await KeysStillOnAsync(source.Id, cancellationToken).ConfigureAwait(false);
        var sourceClient = _clients.For(source);
        var targetClient = _clients.For(target);

        long deleted = 0;
        long kept = 0;

        foreach (var role in StorageMigrationWorker.BucketOrder)
        {
            var sourceBucket = StorageMigrationWorker.BucketName(source, role);
            var targetBucket = StorageMigrationWorker.BucketName(target, role);

            await foreach (var entry in ListOrEmptyAsync(sourceClient, sourceBucket, cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var needed = role == "Files" && stillReferenced.Contains(entry.Key);

                if (!needed
                    && await PresentOnTargetAsync(targetClient, targetBucket, entry, cancellationToken).ConfigureAwait(false))
                {
                    await sourceClient.DeleteObjectAsync(sourceBucket, entry.Key, cancellationToken)
                        .ConfigureAwait(false);
                    deleted++;
                }
                else
                {
                    kept++;
                }

                if ((deleted + kept) % SaveEvery == 0)
                {
                    await SaveProgressAsync(jobId, deleted, kept, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await SaveProgressAsync(jobId, deleted, kept, cancellationToken).ConfigureAwait(false);

        // Reloaded: the counters were written with targeted updates.
        job = await _context.StorageSyncJobs.Find(j => j.Id == jobId)
            .FirstAsync(cancellationToken).ConfigureAwait(false);
        job.CompleteSourcePurge(_clock.UtcNow);

        await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Purged the source {Source} of sync job {JobId}: {Deleted} objects deleted, {Kept} kept.",
            source.Name,
            jobId,
            deleted,
            kept);

        return (deleted, kept);
    }

    private Task<StorageProvider?> LoadProviderAsync(Guid id, CancellationToken cancellationToken)
        => _context.StorageProviders.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    /// <summary>
    /// Keys a file record still reads from the source. After a verified
    /// migration there should be none; any there are must not be deleted.
    /// </summary>
    private async Task<HashSet<string>> KeysStillOnAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var filter = Builders<StoredFile>.Filter;

        var files = await _context.Files
            .Find(filter.Or(
                filter.Eq("Original.ProviderId", providerId),
                filter.Eq("Pdf.ProviderId", providerId),
                filter.Eq("Versions.Object.ProviderId", providerId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (var stored in new[] { file.Original, file.Pdf }
                         .Concat(file.Versions.Select(v => v.Object)))
            {
                if (stored is not null && stored.ProviderId == providerId)
                {
                    keys.Add(stored.Key);
                }
            }
        }

        return keys;
    }

    private static async Task<bool> PresentOnTargetAsync(
        IAmazonS3 target, string bucket, S3Object entry, CancellationToken cancellationToken)
    {
        try
        {
            var head = await target.GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = bucket, Key = entry.Key }, cancellationToken)
                .ConfigureAwait(false);

            return head.Headers.ContentLength == entry.Size;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async IAsyncEnumerable<S3Object> ListOrEmptyAsync(
        IAmazonS3 client,
        string bucket,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = StorageMigrationWorker.ListAsync(client, bucket, startAfter: null, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                try
                {
                    if (!await entries.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchBucket")
                {
                    // A bucket that is not there has nothing to purge.
                    yield break;
                }

                yield return entries.Current;
            }
        }
        finally
        {
            await entries.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task SaveProgressAsync(Guid jobId, long deleted, long kept, CancellationToken cancellationToken)
        => _context.StorageSyncJobs.UpdateOneAsync(
            j => j.Id == jobId,
            Builders<StorageSyncJob>.Update
                .Set(j => j.SourcePurgedObjects, deleted)
                .Set(j => j.SourceKeptObjects, kept),
            cancellationToken);
}

/// <summary>The Hangfire entry point for <see cref="StorageSourcePurge"/>.</summary>
public sealed class StorageSourcePurgeJob
{
    private readonly StorageSourcePurge _purge;

    public StorageSourcePurgeJob(StorageSourcePurge purge) => _purge = purge;

    public Task RunAsync(Guid jobId, CancellationToken cancellationToken)
        => _purge.RunAsync(jobId, cancellationToken);
}
