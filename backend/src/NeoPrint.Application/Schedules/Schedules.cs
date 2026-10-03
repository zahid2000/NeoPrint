using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Notifications;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Schedules;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Schedules;

/// <summary>The <c>scheduled_prints</c> collection (plan section 5).</summary>
public interface IScheduledPrintRepository
{
    Task AddAsync(ScheduledPrint schedule, CancellationToken cancellationToken = default);

    Task<ScheduledPrint?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ScheduledPrint?> FindByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task UpdateAsync(ScheduledPrint schedule, CancellationToken cancellationToken = default);

    /// <summary>The user's scheduled prints, newest first, without the ones cancelled before they were paid.</summary>
    Task<IReadOnlyList<ScheduledPrint>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>Not paid yet and made before <paramref name="createdBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<ScheduledPrint>> ListPendingPaymentAsync(DateTime createdBefore, int take, CancellationToken cancellationToken = default);

    /// <summary><see cref="ScheduledPrintStatus.Scheduled"/> ones whose time is not after <paramref name="dueBy"/>, earliest first.</summary>
    Task<IReadOnlyList<ScheduledPrint>> ListDueAsync(DateTime dueBy, int take, CancellationToken cancellationToken = default);

    /// <summary><see cref="ScheduledPrintStatus.Scheduled"/> ones not reminded yet whose time is not after <paramref name="remindBy"/>.</summary>
    Task<IReadOnlyList<ScheduledPrint>> ListToRemindAsync(DateTime remindBy, int take, CancellationToken cancellationToken = default);

    /// <summary><see cref="ScheduledPrintStatus.AwaitingUser"/> since before <paramref name="awaitingBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<ScheduledPrint>> ListAwaitingBeforeAsync(DateTime awaitingBefore, int take, CancellationToken cancellationToken = default);

    /// <summary>Whether a scheduled print still waiting to be printed uses this file.</summary>
    Task<bool> HasActiveForFileAsync(Guid fileId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>POST schedules</c> (plan section 9.5, step 1): an order at
/// <paramref name="Kiosk"/> for <paramref name="ScheduledAt"/>, paid in the
/// app through <c>payments/{id}/pay-with-app</c>. <paramref name="ExpectedAmount"/>
/// is the price the app showed (<c>printJobs.priceChanged</c> when the
/// kiosk's prices changed since).
/// </summary>
public sealed record CreateScheduleCommand(
    Guid UserId, string Kiosk, Guid FileId, PrintOptions Options, DateTime ScheduledAt, decimal ExpectedAmount)
    : IRequest<ScheduleCreatedResponse>;

/// <summary><c>GET schedules</c>: the user's scheduled prints, newest first.</summary>
public sealed record ListMySchedulesQuery(Guid UserId) : IRequest<IReadOnlyList<ScheduleView>>;

/// <summary><c>PUT schedules/{id}/time</c> (plan section 9.5, step 4).</summary>
public sealed record RescheduleCommand(Guid UserId, Guid ScheduleId, DateTime ScheduledAt) : IRequest<ScheduleView>;

/// <summary>
/// <c>POST schedules/{id}/print-now</c> (plan section 9.5, step 3): into the
/// kiosk's queue when it is online and has the paper; otherwise the reason,
/// and the app offers another time. Also before the time (<c>docs/DECISIONS.md</c> T-145).
/// </summary>
public sealed record PrintScheduleNowCommand(Guid UserId, Guid ScheduleId) : IRequest<ScheduleView>;

/// <summary><c>DELETE schedules/{id}</c>: cancelled; what was paid goes back to the wallet (plan section 9.5, step 5).</summary>
public sealed record CancelScheduleCommand(Guid UserId, Guid ScheduleId) : IRequest;

/// <summary>The schedule follows its job: paid, or cancelled from elsewhere (an admin, an expired payment).</summary>
public sealed record SyncScheduleWithJobCommand(Guid JobId) : IRequest;

/// <summary>Every minute: reminders, schedules whose time came, and payments that settled without a word.</summary>
public sealed record RunDueSchedulesCommand : IRequest<int>;

/// <summary>Every 10 minutes: due schedules nobody answered within <c>Schedule.AutoCancelHours</c>.</summary>
public sealed record AutoCancelSchedulesCommand : IRequest<int>;

public sealed record ScheduleCreatedResponse(
    Guid ScheduleId,
    Guid JobId,
    string JobCode,
    Guid PaymentId,
    decimal Amount,
    string Currency,
    DateTime PaymentExpiresAt,
    DateTime ScheduledAt);

/// <param name="Status"><see cref="ScheduledPrintStatus"/>; a paid one whose news has not arrived yet reads <c>Scheduled</c>.</param>
/// <param name="JobStatus">Where its order is: after <c>Sent</c>, whether it printed.</param>
/// <param name="PaymentId">The payment opened with it: an unpaid one is paid with <c>payments/{id}/pay-with-app</c>.</param>
/// <param name="AutoCancelAt">For <c>AwaitingUser</c>: cancelled and refunded after this.</param>
public sealed record ScheduleView(
    Guid Id,
    Guid JobId,
    string JobCode,
    Guid FileId,
    string FileName,
    int Pages,
    PrintOptions Options,
    Guid KioskId,
    string? KioskCode,
    string? KioskName,
    decimal Amount,
    string Currency,
    DateTime ScheduledAt,
    string Status,
    string JobStatus,
    Guid PaymentId,
    DateTime? AutoCancelAt,
    DateTime CreatedAt);

/// <inheritdoc cref="CreateScheduleCommand" />
public sealed class CreateScheduleValidator : IValidator<CreateScheduleCommand>
{
    public IEnumerable<ValidationError> Validate(CreateScheduleCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Kiosk) || request.Kiosk.Trim().Length > 64)
        {
            yield return new ValidationError("kiosk", "sessions.kioskRequired");
        }

        if (request.ExpectedAmount <= 0)
        {
            yield return new ValidationError("expectedAmount", "printJobs.expectedAmountRequired");
        }
    }
}

public sealed class ScheduleHandlers :
    IRequestHandler<CreateScheduleCommand, ScheduleCreatedResponse>,
    IRequestHandler<ListMySchedulesQuery, IReadOnlyList<ScheduleView>>,
    IRequestHandler<RescheduleCommand, ScheduleView>,
    IRequestHandler<PrintScheduleNowCommand, ScheduleView>,
    IRequestHandler<CancelScheduleCommand>,
    IRequestHandler<SyncScheduleWithJobCommand>,
    IRequestHandler<RunDueSchedulesCommand, int>,
    IRequestHandler<AutoCancelSchedulesCommand, int>
{
    /// <summary>The earliest a print can be scheduled, from now (plan section 8.2, "min. 15 dəq sonra").</summary>
    public const int MinLeadMinutes = 15;

    /// <summary>Cancelled by its owner; the refund module maps it to <c>ScheduleCancel</c>.</summary>
    public const string CancelledReason = "schedules.reason.cancelled";

    /// <summary>Nobody answered in time; the refund module maps it to <c>ScheduleCancel</c>.</summary>
    public const string ExpiredReason = "schedules.reason.expired";

    /// <summary>Schedules handled per run of a background job; the next run takes the rest.</summary>
    private const int Batch = 100;

    private readonly IScheduledPrintRepository _schedules;
    private readonly AppPaidOrders _orders;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly PrintOrderRules _rules;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _kioskNotifier;
    private readonly IUserNotifier _userNotifier;
    private readonly IEventPublisher _events;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<ScheduleHandlers> _logger;

    public ScheduleHandlers(
        IScheduledPrintRepository schedules,
        AppPaidOrders orders,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        PrintOrderRules rules,
        PrintQueueService queue,
        IKioskNotifier kioskNotifier,
        IUserNotifier userNotifier,
        IEventPublisher events,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<ScheduleHandlers> logger)
    {
        _schedules = schedules;
        _orders = orders;
        _jobs = jobs;
        _kiosks = kiosks;
        _rules = rules;
        _queue = queue;
        _kioskNotifier = kioskNotifier;
        _userNotifier = userNotifier;
        _events = events;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ScheduleCreatedResponse> Handle(CreateScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var at = await CheckTimeAsync(request.ScheduledAt, now, cancellationToken).ConfigureAwait(false);
        var kiosk = await _orders.ChoosableKioskAsync(request.Kiosk, cancellationToken).ConfigureAwait(false);
        var file = await _orders.ReadyFileAsync(request.UserId, request.FileId, cancellationToken).ConfigureAwait(false);
        var (job, payment) = await _orders
            .PlaceAsync(request.UserId, kiosk, file, request.Options, request.ExpectedAmount, PrintJobSource.Scheduled, now, cancellationToken)
            .ConfigureAwait(false);

        var schedule = ScheduledPrint.Create(request.UserId, kiosk.Id, file.Id, job, payment.Id, at, now);
        await _schedules.AddAsync(schedule, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Print {ScheduleId} scheduled at kiosk {KioskId} for {ScheduledAt:o}: job {JobId}, payment {PaymentId}.",
            schedule.Id,
            kiosk.Id,
            at,
            job.Id,
            payment.Id);

        return new ScheduleCreatedResponse(
            schedule.Id, job.Id, job.JobCode, payment.Id, payment.Amount.Amount, payment.Amount.Currency, payment.ExpiresAt, at);
    }

    public async Task<IReadOnlyList<ScheduleView>> Handle(ListMySchedulesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var schedules = await _schedules.ListForUserAsync(request.UserId, 100, cancellationToken).ConfigureAwait(false);
        var jobs = (await _jobs.ListByIdsAsync(schedules.Select(s => s.JobId).ToList(), cancellationToken).ConfigureAwait(false))
            .ToDictionary(job => job.Id);
        var hours = await AutoCancelHoursAsync(cancellationToken).ConfigureAwait(false);
        var kiosks = new Dictionary<Guid, Kiosk?>();
        var views = new List<ScheduleView>();

        foreach (var schedule in schedules)
        {
            if (!kiosks.TryGetValue(schedule.KioskId, out var kiosk))
            {
                kiosk = await _kiosks.FindByIdAsync(schedule.KioskId, cancellationToken).ConfigureAwait(false);
                kiosks[schedule.KioskId] = kiosk;
            }

            if (jobs.GetValueOrDefault(schedule.JobId) is { } job)
            {
                views.Add(View(schedule, job, kiosk, hours));
            }
        }

        return views;
    }

    public async Task<ScheduleView> Handle(RescheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (schedule, job) = await OwnAsync(request.UserId, request.ScheduleId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var at = await CheckTimeAsync(request.ScheduledAt, now, cancellationToken).ConfigureAwait(false);

        if (!job.IsHeld)
        {
            throw new DomainException("schedules.notActive");
        }

        schedule.Reschedule(at, ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId), now);
        await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Scheduled print {ScheduleId} moved to {ScheduledAt:o}.", schedule.Id, at);

        return await ViewAsync(schedule, job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScheduleView> Handle(PrintScheduleNowCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (schedule, job) = await OwnAsync(request.UserId, request.ScheduleId, cancellationToken).ConfigureAwait(false);
        var actor = ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId);
        var now = _clock.UtcNow;

        if (schedule.Status == ScheduledPrintStatus.Sent)
        {
            // Pressed twice (a retry after a lost answer): it is already in.
            return await ViewAsync(schedule, job, cancellationToken).ConfigureAwait(false);
        }

        if (!schedule.IsActive)
        {
            throw new DomainException("schedules.notActive");
        }

        // Sent once already, but the schedule's own save was lost: only that is left to do.
        if (!job.IsInQueue)
        {
            if (!job.IsHeld)
            {
                throw new DomainException("schedules.notActive");
            }

            var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("kiosk");

            if (kiosk.Status != KioskStatus.Online)
            {
                throw new DomainException(UserMessage.Of("schedules.kioskUnavailable", ("kiosk", kiosk.Code), ("status", kiosk.Status)));
            }

            // The printer or the paper may have changed since it was paid.
            await _rules.EnsureCanPrintAsync(kiosk, job, cancellationToken).ConfigureAwait(false);

            job.Release(actor, now);
            await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);

            await _queue.SyncAsync(job, cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(new Contracts.PrintJobQueued(job.Id, job.KioskId, _correlation.CorrelationId), cancellationToken)
                .ConfigureAwait(false);
            await _kioskNotifier.JobAddedAsync(job.KioskId, job.Id, cancellationToken).ConfigureAwait(false);
            await _queue.AnnounceAsync(job.KioskId, cancellationToken).ConfigureAwait(false);
        }

        schedule.MarkSent(actor, now);
        await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Scheduled print {ScheduleId} sent to kiosk {KioskId} as job {JobId}.", schedule.Id, job.KioskId, job.Id);

        return await ViewAsync(schedule, job, cancellationToken).ConfigureAwait(false);
    }

    public async Task Handle(CancelScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (schedule, job) = await OwnAsync(request.UserId, request.ScheduleId, cancellationToken).ConfigureAwait(false);

        if (schedule.Status == ScheduledPrintStatus.Cancelled)
        {
            return;
        }

        var actor = ActorContext.User(request.UserId, correlationId: _correlation.CorrelationId);
        await CloseAsync(schedule, job, CancelledReason, actor, expire: false, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Scheduled print {ScheduleId} cancelled by its owner; what was paid goes back.", schedule.Id);
    }

    public async Task Handle(SyncScheduleWithJobCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _schedules.FindByJobIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) is not { } schedule
            || await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return;
        }

        await SyncAsync(schedule, job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> Handle(RunDueSchedulesCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var system = ActorContext.System(correlationId: _correlation.CorrelationId);
        var handled = 0;

        // A payment whose news was lost; one still being paid is left a minute.
        foreach (var pending in await _schedules.ListPendingPaymentAsync(now.AddMinutes(-1), Batch, cancellationToken).ConfigureAwait(false))
        {
            if (await _jobs.FindByIdAsync(pending.JobId, cancellationToken).ConfigureAwait(false) is { } job)
            {
                handled += await Safely(pending, () => SyncAsync(pending, job, cancellationToken)).ConfigureAwait(false);
            }
        }

        foreach (var due in await _schedules.ListDueAsync(now, Batch, cancellationToken).ConfigureAwait(false))
        {
            handled += await Safely(
                    due,
                    async () =>
                    {
                        due.BecomeDue(system, now);
                        await RemindAsync(due, now, cancellationToken).ConfigureAwait(false);
                    })
                .ConfigureAwait(false);
        }

        var lead = await ReminderLeadMinutesAsync(cancellationToken).ConfigureAwait(false);

        if (lead > 0)
        {
            foreach (var soon in await _schedules.ListToRemindAsync(now.AddMinutes(lead), Batch, cancellationToken).ConfigureAwait(false))
            {
                handled += await Safely(soon, () => RemindAsync(soon, now, cancellationToken)).ConfigureAwait(false);
            }
        }

        return handled;
    }

    public async Task<int> Handle(AutoCancelSchedulesCommand request, CancellationToken cancellationToken)
    {
        var hours = await AutoCancelHoursAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var system = ActorContext.System(correlationId: _correlation.CorrelationId);
        var expired = 0;

        foreach (var schedule in await _schedules.ListAwaitingBeforeAsync(now.AddHours(-hours), Batch, cancellationToken).ConfigureAwait(false))
        {
            if (await _jobs.FindByIdAsync(schedule.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
            {
                continue;
            }

            expired += await Safely(schedule, () => CloseAsync(schedule, job, ExpiredReason, system, expire: true, cancellationToken))
                .ConfigureAwait(false);
        }

        if (expired > 0)
        {
            _logger.LogInformation("{Count} scheduled print(s) unanswered for {Hours} hours were cancelled and refunded.", expired, hours);
        }

        return expired;
    }

    /// <summary>
    /// Cancels the job first - its refund and receipts follow from that, as
    /// for any cancelled order - then closes the schedule. A retry after a
    /// failure in between finds the job cancelled and only closes the schedule.
    /// </summary>
    private async Task CloseAsync(
        ScheduledPrint schedule, PrintJob job, string reason, ActorContext actor, bool expire, CancellationToken cancellationToken)
    {
        if (job.IsInQueue || job.Status == PrintJobStatus.Completed)
        {
            // Already sent to the kiosk: from here it is an ordinary order.
            throw new DomainException("schedules.notActive");
        }

        var now = _clock.UtcNow;

        if (!job.IsFinished)
        {
            await PrintJobCancellation
                .CancelAsync(job, new UserMessage(reason), actor, now, _jobs, _queue, _kioskNotifier, _events, _correlation, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            Close(schedule, expire, actor, now);
            await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrencyException)
        {
            // The job's cancellation event closed it meanwhile (SyncAsync), to the same end.
            var current = await _schedules.FindByIdAsync(schedule.Id, cancellationToken).ConfigureAwait(false);

            if (current is not { Status: ScheduledPrintStatus.Cancelled or ScheduledPrintStatus.Expired })
            {
                throw;
            }
        }
    }

    private static void Close(ScheduledPrint schedule, bool expire, ActorContext actor, DateTime now)
    {
        if (expire)
        {
            schedule.Expire(actor, now);
        }
        else
        {
            schedule.Cancel(actor, now);
        }
    }

    private async Task SyncAsync(ScheduledPrint schedule, PrintJob job, CancellationToken cancellationToken)
    {
        var system = ActorContext.System(correlationId: _correlation.CorrelationId);
        var now = _clock.UtcNow;

        if (schedule.Status == ScheduledPrintStatus.PendingPayment && job.Status == PrintJobStatus.Paid)
        {
            schedule.Confirm(system, now);
            await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Scheduled print {ScheduleId} paid; it waits for {ScheduledAt:o}.", schedule.Id, schedule.ScheduledAt);
        }
        else if (schedule.Status is (ScheduledPrintStatus.PendingPayment or ScheduledPrintStatus.Scheduled or ScheduledPrintStatus.AwaitingUser)
                 && job.Status is (PrintJobStatus.Cancelled or PrintJobStatus.Expired))
        {
            // Never paid, or cancelled from elsewhere (an admin): the job's own refund rules apply.
            // Cancelled for the schedule's own reason, it ends as that handler would have ended it.
            var expired = job.CancelReason is { } stored && UserMessage.Decode(stored).Key == ExpiredReason;
            Close(schedule, expired && schedule.Status == ScheduledPrintStatus.AwaitingUser, system, now);
            await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Scheduled print {ScheduleId} closed: its job {JobId} is {JobStatus}.", schedule.Id, job.Id, job.Status);
        }
    }

    /// <summary>Saves first, then tells: a failed save repeats the run, not the message.</summary>
    private async Task RemindAsync(ScheduledPrint schedule, DateTime now, CancellationToken cancellationToken)
    {
        var tell = schedule.RemindedAt is null;
        schedule.NoteReminded(now);
        await _schedules.UpdateAsync(schedule, cancellationToken).ConfigureAwait(false);

        if (tell)
        {
            await _userNotifier
                .ScheduleDueAsync(
                    schedule.UserId,
                    new UserScheduleUpdate(schedule.Id, schedule.JobId, schedule.ScheduledAt, schedule.Status.ToString()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>One schedule changed by someone else at this moment is theirs; the run goes on.</summary>
    private async Task<int> Safely(ScheduledPrint schedule, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
            return 1;
        }
        catch (ConcurrencyException)
        {
            _logger.LogInformation("Scheduled print {ScheduleId} changed meanwhile; left for the next run.", schedule.Id);
            return 0;
        }
    }

    /// <exception cref="ValidationException">Sooner than 15 minutes, or further than <c>Schedule.MaxDaysAhead</c>.</exception>
    private async Task<DateTime> CheckTimeAsync(DateTime requested, DateTime now, CancellationToken cancellationToken)
    {
        var at = requested.Kind switch
        {
            DateTimeKind.Utc => requested,
            DateTimeKind.Local => requested.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requested, DateTimeKind.Utc),
        };

        if (at < now.AddMinutes(MinLeadMinutes))
        {
            throw new ValidationException([new ValidationError("scheduledAt", UserMessage.Of("schedules.tooSoon", ("minutes", MinLeadMinutes)))]);
        }

        var days = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.ScheduleMaxDaysAhead, 30, cancellationToken).ConfigureAwait(false), 1, 365);

        return at > now.AddDays(days)
            ? throw new ValidationException([new ValidationError("scheduledAt", UserMessage.Of("schedules.tooFar", ("days", days)))])
            : at;
    }

    private async Task<(ScheduledPrint Schedule, PrintJob Job)> OwnAsync(Guid userId, Guid scheduleId, CancellationToken cancellationToken)
    {
        var schedule = await _schedules.FindByIdAsync(scheduleId, cancellationToken).ConfigureAwait(false);

        if (schedule is null || schedule.UserId != userId
            || await _jobs.FindByIdAsync(schedule.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            throw new NotFoundException("schedule");
        }

        return (schedule, job);
    }

    private async Task<ScheduleView> ViewAsync(ScheduledPrint schedule, PrintJob job, CancellationToken cancellationToken)
    {
        var kiosk = await _kiosks.FindByIdAsync(schedule.KioskId, cancellationToken).ConfigureAwait(false);
        return View(schedule, job, kiosk, await AutoCancelHoursAsync(cancellationToken).ConfigureAwait(false));
    }

    private static ScheduleView View(ScheduledPrint schedule, PrintJob job, Kiosk? kiosk, int autoCancelHours)
    {
        var status = schedule.Status == ScheduledPrintStatus.PendingPayment && job.Status == PrintJobStatus.Paid
            ? ScheduledPrintStatus.Scheduled
            : schedule.Status;

        return new ScheduleView(
            schedule.Id,
            job.Id,
            job.JobCode,
            schedule.FileId,
            job.LocalFileName ?? string.Empty,
            job.PageCount,
            job.Options,
            schedule.KioskId,
            kiosk?.Code,
            kiosk?.Name,
            job.Price.Total.Amount,
            job.Price.Total.Currency,
            schedule.ScheduledAt,
            status.ToString(),
            job.Status.ToString(),
            schedule.PaymentId,
            schedule.AwaitingSince?.AddHours(autoCancelHours),
            schedule.CreatedAt);
    }

    private async Task<int> AutoCancelHoursAsync(CancellationToken cancellationToken)
        => Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.ScheduleAutoCancelHours, 24, cancellationToken).ConfigureAwait(false), 1, 720);

    private async Task<int> ReminderLeadMinutesAsync(CancellationToken cancellationToken)
        => Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.ScheduleReminderLeadMinutes, 0, cancellationToken).ConfigureAwait(false), 0, 1440);
}
