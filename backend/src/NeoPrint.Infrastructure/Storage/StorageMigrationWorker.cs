using System.Security.Cryptography;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>Told about a sync job's progress, for the live view in the panel.</summary>
public interface IStorageSyncObserver
{
    /// <summary>Called after each object and on every status change.</summary>
    Task ProgressAsync(StorageSyncJob job, CancellationToken cancellationToken);
}

/// <summary>An observer that does nothing, for when nobody is watching.</summary>
public sealed class NullStorageSyncObserver : IStorageSyncObserver
{
    public static readonly NullStorageSyncObserver Instance = new();

    public Task ProgressAsync(StorageSyncJob job, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Copies every object from one provider to another (plan sections 2.10, 9.8).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Restart-safe.</strong> After every object the checkpoint - the
/// bucket and the last key copied - is written to the job. S3 lists keys in
/// lexicographic order, so a worker that picks the job up again resumes with
/// <c>StartAfter</c> that key and copies only what is left. Which worker that
/// is, is decided by the lease: a job whose lease has lapsed belongs to
/// whoever claims it next, and a job with a live lease belongs to nobody else.
/// </para>
/// <para>
/// <strong>Nothing is decrypted.</strong> Objects are encrypted before they are
/// stored, and the keys do not depend on the provider, so the ciphertext is
/// copied as it is. Each copy is verified by reading it back from the target
/// and comparing SHA-256 and length - a copy that has only been written, not
/// read, has not been proven.
/// </para>
/// <para>
/// <strong>No gap.</strong> While a migration is active, new writes go to both
/// providers (<see cref="S3FileStorage"/>'s dual-write). The verification pass
/// then repairs whatever is still missing on the target - an object that
/// arrived while the listing was past its key, or a dual-write that failed -
/// before any reference is switched.
/// </para>
/// </remarks>
public sealed class StorageMigrationWorker
{
    /// <summary>How long a lease lasts without being renewed.</summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>Attempts per object before it is recorded as a failure (plan section 2.10).</summary>
    public const int MaxAttempts = 3;

    /// <summary>The order buckets are copied in, by role. The checkpoint names one of these.</summary>
    public static readonly string[] BucketOrder = ["Files", "Receipts", "Temp"];

    private readonly MongoContext _context;
    private readonly StorageProviderRegistry _registry;
    private readonly S3ClientFactory _clients;
    private readonly IClock _clock;
    private readonly IStorageSyncObserver _observer;
    private readonly IEventPublisher? _events;
    private readonly ILogger<StorageMigrationWorker> _logger;

    /// <summary>Identifies this worker in the lease.</summary>
    public string WorkerId { get; }

    public StorageMigrationWorker(
        MongoContext context,
        StorageProviderRegistry registry,
        S3ClientFactory clients,
        IClock clock,
        IStorageSyncObserver observer,
        ILogger<StorageMigrationWorker> logger,
        IEventPublisher? events = null,
        string? workerId = null)
    {
        _context = context;
        _registry = registry;
        _clients = clients;
        _clock = clock;
        _observer = observer;
        _events = events;
        _logger = logger;
        WorkerId = workerId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Runs a job until it is finished, paused, cancelled or this process is
    /// stopped.
    /// </summary>
    /// <returns>
    /// The job as it was left, or null when another worker holds it or there
    /// is nothing to run.
    /// </returns>
    public async Task<StorageSyncJob?> RunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await TryClaimAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            return null;
        }

        var actor = ActorContext.System("Storage sinxronizasiyası");

        try
        {
            if (job.Status == StorageSyncStatus.Pending)
            {
                await CountAsync(job, cancellationToken).ConfigureAwait(false);
                job.Start(actor, _clock.UtcNow);
                job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);
                _registry.Invalidate();
            }

            if (job.Status == StorageSyncStatus.Running)
            {
                var finished = await CopyAllAsync(job, cancellationToken).ConfigureAwait(false);

                job = finished.Job;

                if (!finished.Completed)
                {
                    // Paused or cancelled by an administrator; the lease is
                    // released so a resume can start straight away.
                    return job;
                }

                job.BeginVerification(actor, _clock.UtcNow);
                job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }

            if (job.Status == StorageSyncStatus.Verifying)
            {
                job = await VerifyAndSwitchAsync(job, actor, cancellationToken).ConfigureAwait(false);
            }

            return job;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping. The job keeps its checkpoint and its
            // lease; the lease lapses and the next worker resumes it.
            _logger.LogInformation(
                "Sync job {JobId} interrupted at {Bucket}/{Key}; it will resume from there.",
                job.Id,
                job.Checkpoint?.Bucket,
                job.Checkpoint?.LastKey);

            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Sync job {JobId} failed.", job.Id);

            var latest = await LoadAsync(job.Id, CancellationToken.None).ConfigureAwait(false) ?? job;

            if (latest.IsActive && latest.Status != StorageSyncStatus.Paused)
            {
                latest.Fail("Sinxronizasiya gözlənilməz xəta ilə dayandı. Təfərrüat log-dadır.", actor, _clock.UtcNow);
                await SaveAsync(latest, CancellationToken.None).ConfigureAwait(false);
                _registry.Invalidate();
            }

            return latest;
        }
    }

    /// <summary>
    /// Claims the job for this worker, or returns null.
    /// </summary>
    /// <remarks>
    /// One atomic update: two workers racing for the same lapsed lease cannot
    /// both win, because only one of them matches the filter.
    /// </remarks>
    private async Task<StorageSyncJob?> TryClaimAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var filter = Builders<StorageSyncJob>.Filter;

        var claimable = filter.And(
            filter.Eq(job => job.Id, jobId),
            filter.In(job => job.Status, [StorageSyncStatus.Pending, StorageSyncStatus.Running, StorageSyncStatus.Verifying]),
            filter.Or(
                filter.Eq(job => job.LeaseExpiresAt, null),
                filter.Lt(job => job.LeaseExpiresAt, now),
                filter.Eq(job => job.LeaseOwner, WorkerId)));

        var update = Builders<StorageSyncJob>.Update
            .Set(job => job.LeaseOwner, WorkerId)
            .Set(job => job.LeaseExpiresAt, now + LeaseDuration)
            .Inc(job => job.RowVersion, 1);

        var claimed = await _context.StorageSyncJobs
            .FindOneAndUpdateAsync(
                claimable,
                update,
                new FindOneAndUpdateOptions<StorageSyncJob> { ReturnDocument = ReturnDocument.After },
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            _logger.LogDebug("Sync job {JobId} is not claimable by {WorkerId}.", jobId, WorkerId);
        }

        return claimed;
    }

    /// <summary>Counts what there is to copy, once, when the job first starts.</summary>
    private async Task CountAsync(StorageSyncJob job, CancellationToken cancellationToken)
    {
        var source = await _registry.GetByIdAsync(job.SourceId, cancellationToken).ConfigureAwait(false);
        var client = _clients.For(source);

        long objects = 0;
        long bytes = 0;

        foreach (var role in BucketOrder)
        {
            await foreach (var entry in ListAsync(client, BucketName(source, role), startAfter: null, cancellationToken)
                               .ConfigureAwait(false))
            {
                objects++;
                bytes += entry.Size ?? 0;
            }
        }

        job.TotalObjects = objects;
        job.TotalBytes = bytes;
    }

    private async Task<(StorageSyncJob Job, bool Completed)> CopyAllAsync(
        StorageSyncJob job, CancellationToken cancellationToken)
    {
        var source = await _registry.GetByIdAsync(job.SourceId, cancellationToken).ConfigureAwait(false);
        var target = await _registry.GetByIdAsync(job.TargetId, cancellationToken).ConfigureAwait(false);
        var sourceClient = _clients.For(source);
        var targetClient = _clients.For(target);

        var startIndex = job.Checkpoint is null ? 0 : Array.IndexOf(BucketOrder, job.Checkpoint.Bucket);

        for (var index = Math.Max(0, startIndex); index < BucketOrder.Length; index++)
        {
            var role = BucketOrder[index];

            // Only the checkpoint's own bucket resumes mid-way; later buckets
            // start from the beginning.
            var startAfter = job.Checkpoint is not null && job.Checkpoint.Bucket == role
                ? job.Checkpoint.LastKey
                : null;

            await foreach (var entry in ListAsync(sourceClient, BucketName(source, role), startAfter, cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var copied = await CopyWithRetryAsync(
                        sourceClient, BucketName(source, role),
                        targetClient, BucketName(target, role),
                        entry.Key, cancellationToken)
                    .ConfigureAwait(false);

                if (copied.Error is null)
                {
                    job.CopiedObjects++;
                    job.CopiedBytes += copied.Bytes;
                    await ResolveFailureAsync(job.Id, role, entry.Key, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    job.FailedObjects++;
                    await RecordFailureAsync(job.Id, role, entry.Key, copied.Error, cancellationToken)
                        .ConfigureAwait(false);
                }

                job.Checkpoint = new StorageSyncCheckpoint(role, entry.Key);
                job.LeaseExpiresAt = _clock.UtcNow + LeaseDuration;

                job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);

                await _observer.ProgressAsync(job, cancellationToken).ConfigureAwait(false);

                // An administrator paused or cancelled it; SaveAsync picked
                // that up from the stored document.
                if (job.Status is StorageSyncStatus.Paused or StorageSyncStatus.Cancelled)
                {
                    job.ReleaseLease();
                    job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    await _observer.ProgressAsync(job, cancellationToken).ConfigureAwait(false);

                    return (job, false);
                }
            }

            // Past this bucket: the checkpoint moves to the start of the next
            // one, so a resume does not list this bucket again.
            if (index + 1 < BucketOrder.Length)
            {
                job.Checkpoint = new StorageSyncCheckpoint(BucketOrder[index + 1], null);
                job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }

        return (job, true);
    }

    /// <summary>
    /// Checks the target has every object, repairs what is missing, then
    /// switches the references and, if asked, the primary.
    /// </summary>
    private async Task<StorageSyncJob> VerifyAndSwitchAsync(
        StorageSyncJob job, ActorContext actor, CancellationToken cancellationToken)
    {
        var source = await _registry.GetByIdAsync(job.SourceId, cancellationToken).ConfigureAwait(false);
        var target = await _registry.GetByIdAsync(job.TargetId, cancellationToken).ConfigureAwait(false);
        var sourceClient = _clients.For(source);
        var targetClient = _clients.For(target);

        var stillMissing = new List<string>();

        foreach (var role in BucketOrder)
        {
            var targetBucket = BucketName(target, role);
            var present = new Dictionary<string, long>(StringComparer.Ordinal);

            try
            {
                await foreach (var entry in ListAsync(targetClient, targetBucket, startAfter: null, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    present[entry.Key] = entry.Size ?? 0;
                }
            }
            catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchBucket")
            {
                // A bucket missing on the target holds nothing: every object
                // for it goes to the repair below, which fails with a reason
                // the panel can show rather than an unexplained error.
                _logger.LogWarning("Bucket {Bucket} does not exist on the migration target.", targetBucket);
            }

            await foreach (var entry in ListAsync(sourceClient, BucketName(source, role), startAfter: null, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (present.TryGetValue(entry.Key, out var size) && size == (entry.Size ?? 0))
                {
                    continue;
                }

                // Written after the listing had passed its key, or a
                // dual-write that did not land: copy it now rather than fail.
                var repaired = await CopyWithRetryAsync(
                        sourceClient, BucketName(source, role),
                        targetClient, targetBucket,
                        entry.Key, cancellationToken)
                    .ConfigureAwait(false);

                if (repaired.Error is null)
                {
                    await ResolveFailureAsync(job.Id, role, entry.Key, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    stillMissing.Add($"{role}/{entry.Key}");
                    await RecordFailureAsync(job.Id, role, entry.Key, repaired.Error, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        if (stillMissing.Count > 0)
        {
            // Nothing is switched: the source is still the only complete copy,
            // and every file must keep pointing at it.
            job.FailedObjects = stillMissing.Count;
            job.Fail(
                $"{stillMissing.Count} obyekt hədəfə köçürülə bilmədi. "
                + "Uğursuzları təkrarlayın; o vaxta qədər fayllar mənbədən oxunur.",
                actor,
                _clock.UtcNow);

            job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);
            _registry.Invalidate();
            await _observer.ProgressAsync(job, cancellationToken).ConfigureAwait(false);

            return job;
        }

        if (job.Mode == StorageSyncMode.Migration)
        {
            await SwitchReferencesAsync(job.SourceId, job.TargetId, cancellationToken).ConfigureAwait(false);

            if (job.SwitchPrimaryOnComplete)
            {
                await SwitchPrimaryAsync(source, target, actor, cancellationToken).ConfigureAwait(false);
            }
        }

        job.FailedObjects = 0;
        job.Complete(actor, _clock.UtcNow);
        job = await SaveAsync(job, cancellationToken).ConfigureAwait(false);

        _registry.Invalidate();

        if (_events is not null)
        {
            await _events.PublishAsync(new Contracts.StorageConfigChanged(job.TargetId), cancellationToken)
                .ConfigureAwait(false);
        }

        await _observer.ProgressAsync(job, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sync job {JobId} completed: {Copied} objects, {Bytes} bytes.",
            job.Id,
            job.CopiedObjects,
            job.CopiedBytes);

        return job;
    }

    /// <summary>
    /// Points every document that recorded the source provider at the target.
    /// </summary>
    /// <remarks>
    /// Only after verification: reads follow the recorded provider, so
    /// switching a reference before its object is proven on the target would
    /// make that file unreadable. Idempotent - a second run finds nothing left
    /// to switch. Receipts join this list when they are stored (step 4).
    /// </remarks>
    private async Task SwitchReferencesAsync(Guid sourceId, Guid targetId, CancellationToken cancellationToken)
    {
        var files = _context.Files;
        var filter = Builders<StoredFile>.Filter;
        var update = Builders<StoredFile>.Update;

        await files.UpdateManyAsync(
                filter.Eq("Original.ProviderId", sourceId),
                update.Set("Original.ProviderId", targetId),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        await files.UpdateManyAsync(
                filter.Eq("Pdf.ProviderId", sourceId),
                update.Set("Pdf.ProviderId", targetId),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        await files.UpdateManyAsync(
                filter.Eq("Versions.Object.ProviderId", sourceId),
                update.Set("Versions.$[version].Object.ProviderId", targetId),
                new UpdateOptions
                {
                    ArrayFilters =
                    [
                        new BsonDocumentArrayFilterDefinition<MongoDB.Bson.BsonDocument>(
                            new MongoDB.Bson.BsonDocument("version.Object.ProviderId", new MongoDB.Bson.BsonBinaryData(sourceId, MongoDB.Bson.GuidRepresentation.Standard))),
                    ],
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SwitchPrimaryAsync(
        StorageProvider source, StorageProvider target, ActorContext actor, CancellationToken cancellationToken)
    {
        // Fresh copies: the registry's may be behind what is stored.
        var storedSource = await _context.StorageProviders.Find(p => p.Id == source.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var storedTarget = await _context.StorageProviders.Find(p => p.Id == target.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (storedSource is null || storedTarget is null)
        {
            return;
        }

        // The old primary steps out of the write path first, so there is
        // never a moment with two primaries. It stays readable: its objects
        // are kept for Storage.SourceRetentionDays (plan section 2.10).
        storedSource.Deactivate(actor, _clock.UtcNow);
        await AggregateWriter.ReplaceAsync(_context, _context.StorageProviders, storedSource, cancellationToken)
            .ConfigureAwait(false);

        storedTarget.MakePrimary(actor, _clock.UtcNow);
        await AggregateWriter.ReplaceAsync(_context, _context.StorageProviders, storedTarget, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Storage provider {Target} is now primary; {Source} was deactivated.",
            storedTarget.Name,
            storedSource.Name);
    }

    /// <summary>
    /// Saves the job, merging with an administrator's change if one landed
    /// in between.
    /// </summary>
    /// <remarks>
    /// The worker writes progress and the panel writes status, both to the
    /// same document. When the row version says the panel got there first,
    /// the stored document is reloaded - it carries the new status - and this
    /// worker's progress is laid over it.
    /// </remarks>
    private async Task<StorageSyncJob> SaveAsync(StorageSyncJob job, CancellationToken cancellationToken)
    {
        try
        {
            await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, job, cancellationToken)
                .ConfigureAwait(false);

            return job;
        }
        catch (ConcurrencyException)
        {
            var stored = await LoadAsync(job.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Sync job {job.Id} disappeared while running.");

            stored.TotalObjects = job.TotalObjects;
            stored.TotalBytes = job.TotalBytes;
            stored.CopiedObjects = job.CopiedObjects;
            stored.CopiedBytes = job.CopiedBytes;
            stored.FailedObjects = job.FailedObjects;
            stored.Checkpoint = job.Checkpoint;

            if (stored.IsActive && stored.Status != StorageSyncStatus.Paused)
            {
                stored.LeaseOwner = job.LeaseOwner;
                stored.LeaseExpiresAt = job.LeaseExpiresAt;
            }

            await AggregateWriter.ReplaceAsync(_context, _context.StorageSyncJobs, stored, cancellationToken)
                .ConfigureAwait(false);

            return stored;
        }
    }

    private async Task<StorageSyncJob?> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _context.StorageSyncJobs.Find(job => job.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task<(long Bytes, string? Error)> CopyWithRetryAsync(
        IAmazonS3 source,
        string sourceBucket,
        IAmazonS3 target,
        string targetBucket,
        string key,
        CancellationToken cancellationToken)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var bytes = await S3ObjectCopier.CopyAsync(source, sourceBucket, target, targetBucket, key, cancellationToken)
                    .ConfigureAwait(false);

                return (bytes, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception is AmazonS3Exception s3
                    ? $"{s3.ErrorCode ?? s3.StatusCode.ToString()}"
                    : exception.GetType().Name;

                _logger.LogWarning(
                    exception,
                    "Copying {Key} failed on attempt {Attempt} of {MaxAttempts}.",
                    key,
                    attempt,
                    MaxAttempts);

                if (attempt < MaxAttempts)
                {
                    // Exponential: 1s, 2s (plan section 2.10).
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return (0, lastError ?? "Naməlum xəta");
    }

    private async Task RecordFailureAsync(
        Guid jobId, string bucket, string key, string error, CancellationToken cancellationToken)
    {
        var filter = Builders<StorageSyncFailure>.Filter;

        await _context.StorageSyncFailures.UpdateOneAsync(
                filter.And(
                    filter.Eq(f => f.JobId, jobId),
                    filter.Eq(f => f.Bucket, bucket),
                    filter.Eq(f => f.Key, key)),
                Builders<StorageSyncFailure>.Update
                    .SetOnInsert(f => f.Id, Guid.NewGuid())
                    .SetOnInsert(f => f.CreatedAt, _clock.UtcNow)
                    .Set(f => f.Error, error)
                    .Set(f => f.LastAttemptAt, _clock.UtcNow)
                    .Unset(f => f.ResolvedAt)
                    .Inc(f => f.Attempts, MaxAttempts),
                new UpdateOptions { IsUpsert = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ResolveFailureAsync(
        Guid jobId, string bucket, string key, CancellationToken cancellationToken)
    {
        var filter = Builders<StorageSyncFailure>.Filter;

        await _context.StorageSyncFailures.UpdateOneAsync(
                filter.And(
                    filter.Eq(f => f.JobId, jobId),
                    filter.Eq(f => f.Bucket, bucket),
                    filter.Eq(f => f.Key, key),
                    filter.Eq(f => f.ResolvedAt, null)),
                Builders<StorageSyncFailure>.Update.Set(f => f.ResolvedAt, _clock.UtcNow),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists a bucket in key order, one page at a time.</summary>
    private static async IAsyncEnumerable<S3Object> ListAsync(
        IAmazonS3 client,
        string bucket,
        string? startAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? continuation = null;

        do
        {
            var page = await client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        StartAfter = continuation is null ? startAfter : null,
                        ContinuationToken = continuation,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in page.S3Objects ?? [])
            {
                yield return entry;
            }

            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (continuation is not null);
    }

    private static string BucketName(StorageProvider provider, string role) => role switch
    {
        "Files" => provider.Buckets.Files,
        "Receipts" => provider.Buckets.Receipts,
        "Temp" => provider.Buckets.Temp,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown bucket role."),
    };
}
