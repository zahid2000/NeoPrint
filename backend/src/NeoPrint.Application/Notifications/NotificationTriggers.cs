using System.Globalization;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Schedules;

namespace NeoPrint.Application.Notifications;

/// <summary>
/// The app screens a notification opens (<c>docs/DECISIONS.md</c>, T-146): the
/// links of plan section 8.3 plus the app's own lists.
/// </summary>
public static class NotificationLinks
{
    public static string Job(Guid jobId) => $"neoprint://jobs/{jobId}";

    public static string Schedule(Guid scheduleId) => $"neoprint://schedules/{scheduleId}";

    public const string Wallet = "neoprint://wallet";

    public const string Files = "neoprint://files";

    /// <summary>The "Alınanlar" list of shared files.</summary>
    public const string Shares = "neoprint://shares";

    /// <summary>The referral screen: the code, the link and what was earned.</summary>
    public const string Referral = "neoprint://referral";

    public const string Export = "neoprint://privacy/export";
}

/// <summary>A job printed: its owner hears it (plan section 8.4, <c>JobCompleted</c>).</summary>
public sealed record NotifyJobCompletedCommand(Guid JobId) : IRequest;

/// <summary>Money went back to a registered user (plan section 8.4, <c>RefundCompleted</c>).</summary>
public sealed record NotifyRefundCompletedCommand(Guid RefundId) : IRequest;

/// <summary>
/// A registered user's order stopped at the printer (plan section 9.6, A.5;
/// 8.4 <c>JobInterruptedRefundOffer</c>): wait, or cancel it and take the
/// money back.
/// </summary>
public sealed record NotifyJobInterruptedCommand(Guid JobId) : IRequest;

/// <summary>
/// A kiosk's printer stopped (plan section 8.4, <c>KioskOutOfServiceRefundOffer</c>):
/// every registered user with an order waiting there is told and offered the
/// same choice.
/// </summary>
/// <returns>How many owners were told.</returns>
public sealed record NotifyAffectedUsersCommand(Guid KioskId) : IRequest<int>;

/// <summary>
/// What each event tells the user. Each sends one <see cref="NotifyUserCommand"/>
/// with a key that makes a repeat of the event a no-op.
/// </summary>
public sealed class NotificationTriggers :
    IRequestHandler<NotifyJobCompletedCommand>,
    IRequestHandler<NotifyRefundCompletedCommand>,
    IRequestHandler<NotifyJobInterruptedCommand>,
    IRequestHandler<NotifyAffectedUsersCommand, int>
{
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly IRefundRepository _refunds;
    private readonly ISender _sender;

    public NotificationTriggers(IPrintJobRepository jobs, IKioskRepository kiosks, IRefundRepository refunds, ISender sender)
    {
        _jobs = jobs;
        _kiosks = kiosks;
        _refunds = refunds;
        _sender = sender;
    }

    /// <summary>The time of a scheduled print came (plan section 9.5, step 2).</summary>
    public static NotifyUserCommand ScheduleDue(ScheduledPrint schedule, string kiosk)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new NotifyUserCommand(
            schedule.UserId,
            NotificationType.ScheduleDue,
            UserMessage.Of("notifications.scheduleDue.title"),
            UserMessage.Of("notifications.scheduleDue.body", ("kiosk", kiosk)),
            NotificationLinks.Schedule(schedule.Id),
            // A new time is a new reminder; the same time, once.
            $"ScheduleDue:{schedule.Id}:{schedule.ScheduledAt.Ticks}");
    }

    /// <summary>Nobody answered in time: cancelled, the money back on the balance (plan section 9.5, step 4).</summary>
    public static NotifyUserCommand ScheduleAutoCancelled(ScheduledPrint schedule, string kiosk)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new NotifyUserCommand(
            schedule.UserId,
            NotificationType.ScheduleAutoCancelled,
            UserMessage.Of("notifications.scheduleAutoCancelled.title"),
            UserMessage.Of("notifications.scheduleAutoCancelled.body", ("kiosk", kiosk), ("amount", Amount(schedule.Price.Total.Amount))),
            NotificationLinks.Schedule(schedule.Id),
            $"ScheduleAutoCancelled:{schedule.Id}");
    }

    /// <summary>
    /// Money always with two decimals ("0,20 ₼", docs/DESIGN_SYSTEM.md section 6): a
    /// decimal read back from Mongo has lost its scale (0.2).
    /// </summary>
    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public async Task Handle(NotifyJobCompletedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        if (job is not { UserId: { } userId, Status: PrintJobStatus.Completed })
        {
            return;
        }

        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        await _sender
            .Send(
                new NotifyUserCommand(
                    userId,
                    NotificationType.JobCompleted,
                    UserMessage.Of("notifications.jobCompleted.title"),
                    UserMessage.Of("notifications.jobCompleted.body", ("code", job.JobCode), ("kiosk", kiosk?.Code ?? string.Empty)),
                    NotificationLinks.Job(job.Id),
                    $"JobCompleted:{job.Id}"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task Handle(NotifyJobInterruptedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is not { UserId: not null, Status: PrintJobStatus.Interrupted })
        {
            return;
        }

        var kiosk = await _kiosks.FindByIdAsync(job.KioskId, cancellationToken).ConfigureAwait(false);

        await OfferRefundAsync(
                job,
                NotificationType.JobInterruptedRefundOffer,
                UserMessage.Of("notifications.jobInterrupted.title"),
                UserMessage.Of("notifications.jobInterrupted.body", ("code", job.JobCode), ("kiosk", kiosk?.Code ?? string.Empty)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> Handle(NotifyAffectedUsersCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        // Back already: the orders print, and nobody needs to decide anything.
        if (kiosk?.FaultSince is null)
        {
            return 0;
        }

        var waiting = (await _jobs.ListQueueAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
            .Where(job => job.Status == PrintJobStatus.Queued)
            .Concat(await _jobs.ListInterruptedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false))
            .Where(job => job.UserId is not null)
            .ToList();

        foreach (var job in waiting)
        {
            await OfferRefundAsync(
                    job,
                    NotificationType.KioskOutOfServiceRefundOffer,
                    UserMessage.Of("notifications.kioskStopped.title"),
                    UserMessage.Of("notifications.kioskStopped.body", ("code", job.JobCode), ("kiosk", kiosk.Code)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return waiting.Count;
    }

    /// <summary>
    /// One offer per time the order waits: the key carries when it last joined
    /// the queue, so the order's own interruption and the kiosk's outage are
    /// one notification, and an order that stops again later is told again.
    /// </summary>
    private Task OfferRefundAsync(
        PrintJob job, NotificationType type, UserMessage title, UserMessage body, CancellationToken cancellationToken)
        => _sender.Send(
            new NotifyUserCommand(
                job.UserId!.Value,
                type,
                title,
                body,
                NotificationLinks.Job(job.Id),
                $"RefundOffer:{job.Id}:{(job.QueuedAt ?? job.CreatedAt).Ticks}"),
            cancellationToken);

    public async Task Handle(NotifyRefundCompletedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false);
        // A scheduled print's money: the user cancelled it in the app and saw the
        // result there, or ScheduleAutoCancelled already says it came back.
        if (refund is not { UserId: { } userId, Status: RefundStatus.Succeeded } || refund.Reason == RefundReason.ScheduleCancel)
        {
            return;
        }

        await _sender
            .Send(
                new NotifyUserCommand(
                    userId,
                    NotificationType.RefundCompleted,
                    UserMessage.Of("notifications.refundCompleted.title"),
                    UserMessage.Of(
                        "notifications.refundCompleted.body",
                        ("amount", Amount(refund.Amount.Amount)),
                        ("destination", refund.Destination.ToString())),
                    NotificationLinks.Wallet,
                    $"RefundCompleted:{refund.Id}"),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
