using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Persistence;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.PrintJobs;

/// <inheritdoc cref="IPrintJobRepository" />
public sealed class PrintJobRepository : IPrintJobRepository
{
    private static readonly PrintJobStatus[] QueueStatuses = [PrintJobStatus.Queued, PrintJobStatus.Printing];

    /// <summary>Jobs whose paper is spoken for: placed, not printed and not given up.</summary>
    private static readonly PrintJobStatus[] ReservingStatuses =
    [
        PrintJobStatus.PendingPayment,
        PrintJobStatus.Paid,
        PrintJobStatus.Queued,
        PrintJobStatus.Printing,
    ];

    private readonly MongoContext _context;

    public PrintJobRepository(MongoContext context) => _context = context;

    public Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PrintJobs.Find(job => job.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<PrintJob?> FindByCodeAsync(Guid kioskId, string jobCode, CancellationToken cancellationToken = default)
        => _context.PrintJobs
            .Find(job => job.KioskId == kioskId && job.JobCode == jobCode)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<bool> TryAddAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            await AggregateWriter.InsertAsync(_context, _context.PrintJobs, job, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MongoWriteException exception)
            when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task UpdateAsync(PrintJob job, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PrintJobs, job, cancellationToken);

    public async Task<IReadOnlyList<PrintJob>> ListQueueAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => await _context.PrintJobs
            .Find(job => job.KioskId == kioskId && QueueStatuses.Contains(job.Status))
            .SortBy(job => job.QueuedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> ListKiosksWithQueueAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _context.PrintJobs
            .Find(job => QueueStatuses.Contains(job.Status))
            .Project(job => job.KioskId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. ids.Distinct()];
    }

    public async Task<int> ReservedSheetsAsync(Guid kioskId, string paper, CancellationToken cancellationToken = default)
    {
        var jobs = await _context.PrintJobs
            .Find(job => job.KioskId == kioskId
                          && job.Options.Paper == paper
                          && ReservingStatuses.Contains(job.Status))
            .Project(job => new { job.RequiredSheets, job.PrintedSheets })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A job that printed some of its sheets before stopping has already
        // taken those off the counter; only the rest is still promised.
        return jobs.Sum(job => Math.Max(0, job.RequiredSheets - job.PrintedSheets));
    }

    public async Task<IReadOnlyList<PrintJob>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        => await _context.PrintJobs
            .Find(job => ids.Contains(job.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<PrintJob> Items, long Total)> SearchAsync(
        PrintJobFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var builder = Builders<PrintJob>.Filter;
        var where = builder.Empty;

        if (filter.KioskIds is { } kiosks)
        {
            where &= builder.In(job => job.KioskId, kiosks);
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            where &= builder.In(job => job.Status, statuses);
        }

        if (filter.From is { } from)
        {
            where &= builder.Gte(job => job.CreatedAt, from);
        }

        if (filter.To is { } to)
        {
            where &= builder.Lt(job => job.CreatedAt, to);
        }

        if (!string.IsNullOrWhiteSpace(filter.JobCode))
        {
            where &= builder.Eq(job => job.JobCode, filter.JobCode);
        }

        var total = await _context.PrintJobs.CountDocumentsAsync(where, cancellationToken).ConfigureAwait(false);
        var items = await _context.PrintJobs
            .Find(where)
            .SortByDescending(job => job.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }
}

/// <inheritdoc cref="ITransitionHistory" />
public sealed class TransitionHistory : ITransitionHistory
{
    private readonly MongoContext _context;

    public TransitionHistory(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<StateTransition>> ListAsync(
        string entityType, Guid entityId, CancellationToken cancellationToken = default)
        => await _context.StateTransitions
            .Find(step => step.EntityType == entityType && step.EntityId == entityId)
            .SortBy(step => step.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <inheritdoc cref="IPrintQueue" />
/// <remarks>
/// One sorted set per kiosk, <c>queue:{kioskId}</c>, scored by the time a job
/// joined, so ZRANGE is the queue in order. Every call tolerates Redis being
/// down: a write is skipped (the reconciliation job restores it) and a read
/// says it could not answer.
/// </remarks>
public sealed class RedisPrintQueue : IPrintQueue
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisPrintQueue> _logger;

    public RedisPrintQueue(IConnectionMultiplexer redis, ILogger<RedisPrintQueue> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public static string KeyFor(Guid kioskId) => $"queue:{kioskId:N}";

    public async Task AddAsync(Guid kioskId, Guid jobId, DateTime queuedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase()
                .SortedSetAddAsync(KeyFor(kioskId), jobId.ToString("N"), queuedAt.Ticks)
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Queue {KioskId}: could not add job {JobId}; reconciliation will.", kioskId, jobId);
        }
    }

    public async Task RemoveAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().SortedSetRemoveAsync(KeyFor(kioskId), jobId.ToString("N")).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Queue {KioskId}: could not remove job {JobId}; reconciliation will.", kioskId, jobId);
        }
    }

    public async Task<IReadOnlyList<Guid>?> ReadAsync(Guid kioskId, CancellationToken cancellationToken = default)
    {
        try
        {
            var members = await _redis.GetDatabase().SortedSetRangeByRankAsync(KeyFor(kioskId)).ConfigureAwait(false);

            return [.. members.Select(member => Guid.ParseExact(member.ToString(), "N"))];
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Queue {KioskId}: Redis did not answer; reading MongoDB.", kioskId);
            return null;
        }
    }

    public async Task ReplaceAsync(
        Guid kioskId, IReadOnlyList<(Guid JobId, DateTime QueuedAt)> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        try
        {
            var transaction = _redis.GetDatabase().CreateTransaction();
            _ = transaction.KeyDeleteAsync(KeyFor(kioskId));

            if (jobs.Count > 0)
            {
                _ = transaction.SortedSetAddAsync(
                    KeyFor(kioskId),
                    [.. jobs.Select(job => new SortedSetEntry(job.JobId.ToString("N"), job.QueuedAt.Ticks))]);
            }

            await transaction.ExecuteAsync().ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(exception, "Queue {KioskId}: could not rebuild from MongoDB.", kioskId);
        }
    }
}

/// <summary>
/// Rebuilds every kiosk's Redis queue from MongoDB, every minute (plan
/// section 3: Redis is a cache; its loss must lose nothing).
/// </summary>
public sealed class PrintQueueReconciliationJob
{
    private readonly IPrintJobRepository _jobs;
    private readonly PrintQueueService _queue;
    private readonly IConnectionMultiplexer _redis;

    public PrintQueueReconciliationJob(IPrintJobRepository jobs, PrintQueueService queue, IConnectionMultiplexer redis)
    {
        _jobs = jobs;
        _queue = queue;
        _redis = redis;
    }

    public const string RecurringId = "print-queue-reconciliation";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var kiosks = (await _jobs.ListKiosksWithQueueAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var kioskId in kiosks)
        {
            var truth = await _jobs.ListQueueAsync(kioskId, cancellationToken).ConfigureAwait(false);
            await _queue.RebuildAsync(kioskId, truth, cancellationToken).ConfigureAwait(false);
        }

        // Sets for kiosks with nothing queued any more: emptied, not left
        // holding jobs that finished while Redis was unreachable.
        foreach (var server in _redis.GetServers())
        {
            await foreach (var key in server.KeysAsync(pattern: "queue:*").WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var text = key.ToString();

                if (Guid.TryParseExact(text["queue:".Length..], "N", out var kioskId) && !kiosks.Contains(kioskId))
                {
                    await _redis.GetDatabase().KeyDeleteAsync(key).ConfigureAwait(false);
                }
            }
        }
    }
}
