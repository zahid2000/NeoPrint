using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.PrintJobs;

/// <summary>
/// A kiosk's queue: kept in Redis and MongoDB together, read in order, and
/// announced to the kiosk and the panels when it changes (plan sections 7.2,
/// 7.4).
/// </summary>
public sealed class PrintQueueService
{
    private readonly IPrintJobRepository _jobs;
    private readonly IPrintQueue _queue;
    private readonly IUserRepository _users;
    private readonly IKioskNotifier _kiosk;
    private readonly IAdminNotifier _admins;

    public PrintQueueService(
        IPrintJobRepository jobs,
        IPrintQueue queue,
        IUserRepository users,
        IKioskNotifier kiosk,
        IAdminNotifier admins)
    {
        _jobs = jobs;
        _queue = queue;
        _users = users;
        _kiosk = kiosk;
        _admins = admins;
    }

    /// <summary>Makes the Redis copy follow the job after a status change.</summary>
    public Task SyncAsync(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.IsInQueue
            ? _queue.AddAsync(job.KioskId, job.Id, job.QueuedAt ?? job.CreatedAt, cancellationToken)
            : _queue.RemoveAsync(job.KioskId, job.Id, cancellationToken);
    }

    /// <summary>The queue in order, from Redis when it answers and from MongoDB when it does not.</summary>
    public async Task<IReadOnlyList<PrintJob>> ReadAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var ids = await _queue.ReadAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (ids is null)
        {
            return await _jobs.ListQueueAsync(kioskId, cancellationToken).ConfigureAwait(false);
        }

        if (ids.Count == 0)
        {
            // An empty set is also what a Redis that lost its data looks like.
            // MongoDB decides, and the set is rebuilt if it was wrong.
            var truth = await _jobs.ListQueueAsync(kioskId, cancellationToken).ConfigureAwait(false);

            if (truth.Count > 0)
            {
                await RebuildAsync(kioskId, truth, cancellationToken).ConfigureAwait(false);
            }

            return truth;
        }

        var loaded = await _jobs.ListByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        var byId = loaded.ToDictionary(job => job.Id);

        // Redis gives the order; MongoDB says what each job is now. A job that
        // left the queue while Redis was unreachable is dropped here and
        // removed by the next reconciliation.
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id]).Where(job => job.IsInQueue)];
    }

    /// <summary>Rewrites the Redis set from MongoDB: the reconciliation job and a lost Redis.</summary>
    public Task RebuildAsync(Guid kioskId, IReadOnlyList<PrintJob> truth, CancellationToken cancellationToken)
        => _queue.ReplaceAsync(
            kioskId,
            [.. truth.Select(job => (job.Id, job.QueuedAt ?? job.CreatedAt))],
            cancellationToken);

    /// <summary>The queue as the panel shows it.</summary>
    public async Task<IReadOnlyList<QueueEntry>> EntriesAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var jobs = await ReadAsync(kioskId, cancellationToken).ConfigureAwait(false);
        var entries = new List<QueueEntry>(jobs.Count);

        foreach (var (job, index) in jobs.Select((job, index) => (job, index)))
        {
            entries.Add(new QueueEntry(
                job.Id,
                job.JobCode,
                index + 1,
                job.Status.ToString(),
                job.Price.Price.Pages,
                job.Options.Copies,
                await InitialAsync(job.UserId, cancellationToken).ConfigureAwait(false),
                job.LocalFileName,
                job.QueuedAt));
        }

        return entries;
    }

    /// <summary>Where the job stands in its kiosk's queue, 1 being next; null when it is not queued.</summary>
    public async Task<int?> PositionAsync(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!job.IsInQueue)
        {
            return null;
        }

        var queue = await ReadAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
        var index = queue.ToList().FindIndex(entry => entry.Id == job.Id);

        return index < 0 ? null : index + 1;
    }

    /// <summary>Tells the kiosk and the panels what the queue is now.</summary>
    public async Task AnnounceAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var entries = await EntriesAsync(kioskId, cancellationToken).ConfigureAwait(false);

        await _kiosk.QueueUpdatedAsync(kioskId, entries, cancellationToken).ConfigureAwait(false);
        await _admins.QueueUpdatedAsync(kioskId, entries, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A job as the API returns it.</summary>
    public async Task<PrintJobResponse> ResponseAsync(PrintJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var price = job.Price;

        return new PrintJobResponse(
            job.Id,
            job.JobCode,
            job.KioskId,
            job.Status.ToString(),
            job.Source.ToString(),
            job.LocalFileName,
            job.PageCount,
            price.Price.Pages,
            job.Options,
            new PriceView(
                price.Price.FinalPrice,
                price.Price.Currency,
                price.Price.BasePrice,
                price.Price.VolumeDiscount,
                price.Price.DiscountPercent,
                price.Price.MinimumApplied,
                price.Price.Sides,
                price.Price.Sheets,
                price.ProfileId,
                price.ProfileName,
                price.TakenAt,
                job.PromoCode,
                job.PromoQuote,
                price.Price.FinalPrice - job.PromoQuote),
            job.RequiredSheets,
            await PositionAsync(job, cancellationToken).ConfigureAwait(false),
            job.PrintedSides,
            job.PrintedSheets,
            Messages.WriteStored(job.ErrorReason),
            Messages.WriteStored(job.CancelReason),
            job.PaymentSkipped,
            job.CreatedAt,
            job.QueuedAt,
            job.FinishedAt);
    }

    private async Task<string?> InitialAsync(Guid? userId, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return null;
        }

        var user = await _users.FindByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
        var name = user?.FullName ?? user?.Email;

        // The first letter and no more: the queue panel is on a public screen
        // (plan section 7.2).
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..1].ToUpperInvariant();
    }
}

/// <summary>Reads the state transitions of one entity, for a history view.</summary>
public interface ITransitionHistory
{
    Task<IReadOnlyList<StateTransition>> ListAsync(
        string entityType, Guid entityId, CancellationToken cancellationToken = default);
}
