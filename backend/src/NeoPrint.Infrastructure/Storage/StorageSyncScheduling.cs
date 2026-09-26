using Hangfire;

using MassTransit;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Storage;
using NeoPrint.Contracts;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Starts sync jobs on the background server, and restarts the ones whose
/// worker has gone.
/// </summary>
/// <remarks>
/// <para>
/// Hangfire runs the worker, and passes its own shutdown token in for the
/// <c>CancellationToken.None</c> written below - so a deployment that stops
/// the process interrupts the copy cleanly, with the checkpoint saved and the
/// lease left to lapse.
/// </para>
/// <para>
/// <see cref="ResumeStaleAsync"/> runs every minute. A job whose lease has
/// lapsed has no worker - the process died, or was redeployed - and is
/// enqueued again; the worker claims it and carries on from the checkpoint.
/// A job with a live lease is left alone, so a slow worker is not doubled.
/// </para>
/// </remarks>
public sealed class StorageSyncScheduler
{
    /// <summary>The recurring job's id in Hangfire.</summary>
    public const string ResumeJobId = "storage-sync-resume";

    private readonly MongoContext _context;
    private readonly IBackgroundJobClient _jobs;
    private readonly IClock _clock;
    private readonly ILogger<StorageSyncScheduler> _logger;

    public StorageSyncScheduler(
        MongoContext context,
        IBackgroundJobClient jobs,
        IClock clock,
        ILogger<StorageSyncScheduler> logger)
    {
        _context = context;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Hands a job to the background server.</summary>
    public string Enqueue(Guid jobId)
        => _jobs.Enqueue<StorageMigrationWorker>(worker => worker.RunAsync(jobId, CancellationToken.None));

    /// <summary>Re-enqueues every active job that has no live worker.</summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task<int> ResumeStaleAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var filter = Builders<StorageSyncJob>.Filter;

        var stale = await _context.StorageSyncJobs
            .Find(filter.And(
                filter.In(job => job.Status, [StorageSyncStatus.Pending, StorageSyncStatus.Running, StorageSyncStatus.Verifying]),
                filter.Or(
                    filter.Eq(job => job.LeaseExpiresAt, null),
                    filter.Lt(job => job.LeaseExpiresAt, now))))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var job in stale)
        {
            _logger.LogInformation(
                "Sync job {JobId} has no live worker ({Status}, lease until {Lease}); resuming it.",
                job.Id,
                job.Status,
                job.LeaseExpiresAt);

            Enqueue(job.Id);
        }

        return stale.Count;
    }
}

/// <summary>
/// Sends sync progress to the panel, at most twice a second per job.
/// </summary>
/// <remarks>
/// The worker reports after every object. On a bucket of small files that is
/// hundreds of messages a second, which the panel cannot draw and the hub
/// should not carry; a status change is always sent, since that is what the
/// administrator is waiting to see.
/// </remarks>
public sealed class AdminStorageSyncObserver : IStorageSyncObserver
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAdminNotifier _notifier;
    private readonly IClock _clock;
    private readonly ILogger<AdminStorageSyncObserver> _logger;

    // Static: the observer is scoped, because the notifier it uses is, and the
    // throttle has to outlive one Hangfire job's scope to mean anything.
    // System.Threading.Lock is .NET 9; this targets .NET 8.
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, (DateTime At, StorageSyncStatus Status)> LastSent = [];

    public AdminStorageSyncObserver(
        IAdminNotifier notifier, IClock clock, ILogger<AdminStorageSyncObserver> logger)
    {
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task ProgressAsync(StorageSyncJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = _clock.UtcNow;

        lock (Gate)
        {
            if (LastSent.TryGetValue(job.Id, out var last)
                && last.Status == job.Status
                && now - last.At < MinimumInterval)
            {
                return;
            }

            // A finished job will not report again; its entry is not kept.
            if (job.IsActive)
            {
                LastSent[job.Id] = (now, job.Status);
            }
            else
            {
                LastSent.Remove(job.Id);
            }
        }

        try
        {
            await _notifier.StorageSyncProgressAsync(
                    new StorageSyncProgress(
                        job.Id,
                        job.Mode.ToString(),
                        job.Status.ToString(),
                        job.TotalObjects,
                        job.CopiedObjects,
                        job.FailedObjects,
                        job.TotalBytes,
                        job.CopiedBytes,
                        job.FailureReason,
                        now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nobody watching, or the hub is restarting: the copy must not
            // stop because the progress bar could not be moved.
            _logger.LogDebug(exception, "Could not send progress for sync job {JobId}.", job.Id);
        }
    }
}

/// <summary>
/// Drops this process's storage cache when any process changes the
/// configuration (plan section 2.10: no restart needed).
/// </summary>
public sealed class StorageConfigChangedConsumer : IConsumer<StorageConfigChanged>
{
    private readonly StorageProviderRegistry _registry;

    public StorageConfigChangedConsumer(StorageProviderRegistry registry) => _registry = registry;

    public Task Consume(ConsumeContext<StorageConfigChanged> context)
    {
        _registry.Invalidate();
        return Task.CompletedTask;
    }
}
