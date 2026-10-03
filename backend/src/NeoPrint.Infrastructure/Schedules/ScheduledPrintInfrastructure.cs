using MediatR;

using MongoDB.Driver;

using NeoPrint.Application.Schedules;
using NeoPrint.Domain.Schedules;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Schedules;

/// <inheritdoc cref="IScheduledPrintRepository" />
public sealed class ScheduledPrintRepository : IScheduledPrintRepository
{
    private static readonly ScheduledPrintStatus[] Active = [ScheduledPrintStatus.Scheduled, ScheduledPrintStatus.AwaitingUser];

    private readonly MongoContext _context;

    public ScheduledPrintRepository(MongoContext context) => _context = context;

    public Task AddAsync(ScheduledPrint schedule, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.ScheduledPrints, schedule, cancellationToken);

    public Task<ScheduledPrint?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.ScheduledPrints.Find(s => s.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<ScheduledPrint?> FindByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _context.ScheduledPrints.Find(s => s.JobId == jobId).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(ScheduledPrint schedule, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.ScheduledPrints, schedule, cancellationToken);

    public async Task<IReadOnlyList<ScheduledPrint>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default)
        => await _context.ScheduledPrints
            .Find(s => s.UserId == userId && !(s.Status == ScheduledPrintStatus.Cancelled && s.PaidAt == null))
            .SortByDescending(s => s.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ScheduledPrint>> ListPendingPaymentAsync(
        DateTime createdBefore, int take, CancellationToken cancellationToken = default)
        => await _context.ScheduledPrints
            .Find(s => s.Status == ScheduledPrintStatus.PendingPayment && s.CreatedAt <= createdBefore)
            .SortBy(s => s.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ScheduledPrint>> ListDueAsync(DateTime dueBy, int take, CancellationToken cancellationToken = default)
        => await _context.ScheduledPrints
            .Find(s => s.Status == ScheduledPrintStatus.Scheduled && s.ScheduledAt <= dueBy)
            .SortBy(s => s.ScheduledAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ScheduledPrint>> ListToRemindAsync(DateTime remindBy, int take, CancellationToken cancellationToken = default)
        => await _context.ScheduledPrints
            .Find(s => s.Status == ScheduledPrintStatus.Scheduled && s.RemindedAt == null && s.ScheduledAt <= remindBy)
            .SortBy(s => s.ScheduledAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ScheduledPrint>> ListAwaitingBeforeAsync(
        DateTime awaitingBefore, int take, CancellationToken cancellationToken = default)
        => await _context.ScheduledPrints
            .Find(s => s.Status == ScheduledPrintStatus.AwaitingUser && s.AwaitingSince <= awaitingBefore)
            .SortBy(s => s.AwaitingSince)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> HasActiveForFileAsync(Guid fileId, CancellationToken cancellationToken = default)
        => _context.ScheduledPrints.Find(s => s.FileId == fileId && Active.Contains(s.Status)).AnyAsync(cancellationToken);
}

/// <summary>
/// Every minute (plan section 6, <c>ScheduledPrintDueJob</c>): a schedule whose
/// time came becomes <c>AwaitingUser</c> and its owner hears <c>ScheduleDue</c>;
/// reminders go out <c>Schedule.ReminderLeadMinutes</c> early.
/// </summary>
/// <remarks>Safe to run twice: each step is a status the schedule either has or not, and a reminder is sent once.</remarks>
public sealed class ScheduledPrintDueJob
{
    public const string RecurringId = "scheduled-print-due";

    private readonly ISender _sender;

    public ScheduledPrintDueJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 120)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new RunDueSchedulesCommand(), cancellationToken);
}

/// <summary>
/// Every 10 minutes (plan section 6, <c>ScheduledPrintAutoCancelJob</c>): a due
/// schedule nobody answered within <c>Schedule.AutoCancelHours</c> is cancelled
/// and its money goes back to the wallet.
/// </summary>
/// <remarks>Safe to run twice: a cancelled job is skipped, and the refund is keyed by the job.</remarks>
public sealed class ScheduledPrintAutoCancelJob
{
    public const string RecurringId = "scheduled-print-auto-cancel";

    private readonly ISender _sender;

    public ScheduledPrintAutoCancelJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new AutoCancelSchedulesCommand(), cancellationToken);
}

/// <summary>
/// A scheduled print follows its order: paid (<c>Scheduled</c>), or cancelled
/// or expired from elsewhere, an admin for one (<c>Cancelled</c>).
/// </summary>
/// <remarks>
/// Most jobs have no schedule; for those this is one indexed miss. A lost
/// message is caught up by <see cref="ScheduledPrintDueJob"/>, a repeated one
/// changes nothing.
/// </remarks>
public sealed class ScheduledPrintJobConsumer :
    MassTransit.IConsumer<Contracts.PaymentSucceeded>,
    MassTransit.IConsumer<Contracts.PrintJobStatusChanged>
{
    private readonly ISender _sender;

    public ScheduledPrintJobConsumer(ISender sender) => _sender = sender;

    public Task Consume(MassTransit.ConsumeContext<Contracts.PaymentSucceeded> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Message.JobId is { } jobId
            ? _sender.Send(new SyncScheduleWithJobCommand(jobId), context.CancellationToken)
            : Task.CompletedTask;
    }

    public Task Consume(MassTransit.ConsumeContext<Contracts.PrintJobStatusChanged> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Message.To is "Cancelled" or "Expired"
            ? _sender.Send(new SyncScheduleWithJobCommand(context.Message.JobId), context.CancellationToken)
            : Task.CompletedTask;
    }
}
