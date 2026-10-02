using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.PrintJobs;

/// <summary>The <c>print_jobs</c> collection (plan section 5).</summary>
/// <summary>The kiosk's reports of each try at printing (plan section 2.5, <c>print_attempts</c>).</summary>
public interface IPrintAttemptRepository
{
    /// <summary>Writes the attempt, replacing the row with the same job and number.</summary>
    Task UpsertAsync(PrintAttempt attempt, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PrintAttempt>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface IPrintJobRepository
{
    Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The job with this code at this kiosk, whatever its status.</summary>
    Task<PrintJob?> FindByCodeAsync(Guid kioskId, string jobCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new job.
    /// </summary>
    /// <returns>False when the job code is already taken, so the caller can draw another.</returns>
    Task<bool> TryAddAsync(PrintJob job, CancellationToken cancellationToken = default);

    /// <exception cref="Domain.Common.ConcurrencyException">Somebody else saved it first.</exception>
    Task UpdateAsync(PrintJob job, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's queue from the source of truth: queued and printing jobs, oldest first.</summary>
    Task<IReadOnlyList<PrintJob>> ListQueueAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>Every kiosk that has something in its queue, for rebuilding the Redis copy.</summary>
    Task<IReadOnlyList<Guid>> ListKiosksWithQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sheets of <paramref name="paper"/> already promised to jobs at this
    /// kiosk that have not printed yet: waiting for payment, paid, queued or
    /// printing (plan section 2.15).
    /// </summary>
    Task<int> ReservedSheetsAsync(Guid kioskId, string paper, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether an order for this file is still open - not yet paid, waiting,
    /// printing, stopped or under review: its PDF must stay as it is until then.
    /// </summary>
    Task<bool> HasOpenOrderForFileAsync(Guid fileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PrintJob>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>A customer's own jobs, newest first (plan section 6: <c>GET print-jobs/my</c>).</summary>
    Task<IReadOnlyList<PrintJob>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>Jobs still waiting for a payment that were placed before <paramref name="createdBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<PrintJob>> ListAwaitingPaymentAsync(DateTime createdBefore, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queued jobs queued, and printing jobs started, before <paramref name="activityBefore"/>:
    /// the ones the watchdog checks against their kiosk's heartbeat (plan section 2.5, scenario B).
    /// </summary>
    Task<IReadOnlyList<PrintJob>> ListStuckCandidatesAsync(DateTime activityBefore, int take, CancellationToken cancellationToken = default);

    /// <summary>Suspected jobs at these kiosks, the longest-waiting first (plan section 8A.3, "Baxış gözləyən job-lar").</summary>
    Task<IReadOnlyList<PrintJob>> ListSuspectedAsync(IReadOnlyCollection<Guid> kioskIds, int take, CancellationToken cancellationToken = default);

    /// <summary>Suspected jobs since before <paramref name="suspectedBefore"/> whose admins were not reminded yet.</summary>
    Task<IReadOnlyList<PrintJob>> ListSuspectedForReminderAsync(DateTime suspectedBefore, int take, CancellationToken cancellationToken = default);

    /// <summary>The admin list, newest first.</summary>
    Task<(IReadOnlyList<PrintJob> Items, long Total)> SearchAsync(
        PrintJobFilter filter, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>What the admin "Print job-lar" page filters on (plan section 8A.3).</summary>
/// <param name="KioskIds">Null for every kiosk the admin may see; the handler narrows it to their scope.</param>
public sealed record PrintJobFilter(
    IReadOnlyCollection<Guid>? KioskIds,
    IReadOnlyCollection<PrintJobStatus>? Statuses,
    DateTime? From,
    DateTime? To,
    string? JobCode);

/// <summary>
/// A kiosk's queue in Redis: a sorted set <c>queue:{kioskId}</c> of job ids,
/// scored by when each joined (plan step 3).
/// </summary>
/// <remarks>
/// A copy, not the truth: MongoDB is. Every change writes both, and the
/// reconciliation job rebuilds the set from MongoDB each minute, so losing
/// Redis loses nothing (plan section 3: "Redis itsə state Mongo-dan bərpa
/// olunur"). A read that cannot reach Redis falls back to MongoDB.
/// </remarks>
public interface IPrintQueue
{
    Task AddAsync(Guid kioskId, Guid jobId, DateTime queuedAt, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>The job ids in queue order, or null when Redis cannot answer.</summary>
    Task<IReadOnlyList<Guid>?> ReadAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>Makes the set exactly these jobs, in this order.</summary>
    Task ReplaceAsync(
        Guid kioskId, IReadOnlyList<(Guid JobId, DateTime QueuedAt)> jobs, CancellationToken cancellationToken = default);
}

/// <summary>Real-time messages to a kiosk over <c>/hubs/kiosk</c> (plan section 6).</summary>
public interface IKioskNotifier
{
    /// <summary>The queue changed; the kiosk refreshes its panel and picks its next job.</summary>
    Task QueueUpdatedAsync(Guid kioskId, IReadOnlyList<QueueEntry> queue, CancellationToken cancellationToken = default);

    Task JobAddedAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>A job was cancelled; a kiosk printing it stops.</summary>
    Task JobCancelledAsync(Guid kioskId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>The selected printer or its capabilities changed (plan section 7.7).</summary>
    Task PrinterConfigChangedAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>Limits, prices, flags or paper changed: fetch the configuration again.</summary>
    Task KioskConfigChangedAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>A remote command for the kiosk (plan section 7.8).</summary>
    Task KioskCommandAsync(Guid kioskId, KioskCommandMessage command, CancellationToken cancellationToken = default);

    /// <summary>A phone joined the kiosk (plan section 9.3): a small notice on its screen, never who.</summary>
    Task SessionOpenedAsync(Guid kioskId, Guid sessionId, DateTime expiresAt, CancellationToken cancellationToken = default);

    /// <summary>The phone left: closed, replaced or expired.</summary>
    Task SessionClosedAsync(Guid kioskId, Guid sessionId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>One line of the queue panel (plan section 7.2).</summary>
/// <param name="Position">1 for the job at the printer or next to it.</param>
/// <param name="UserInitial">The first letter of the owner's name; null for a guest.</param>
public sealed record QueueEntry(
    Guid JobId,
    string JobCode,
    int Position,
    string Status,
    int Pages,
    int Copies,
    string? UserInitial,
    string? FileName,
    DateTime? QueuedAt);

/// <summary>A command pushed to a kiosk.</summary>
public sealed record KioskCommandMessage(Guid CommandId, string Type, string? Payload);

/// <summary>
/// Whether a job may skip its payment (plan step 3: Development only).
/// </summary>
/// <remarks>
/// Set by the API host from its environment, never by configuration a
/// deployment could change: Production cannot turn it on.
/// </remarks>
public sealed class PrintJobOptions
{
    public bool AllowPaymentSkip { get; set; }
}
