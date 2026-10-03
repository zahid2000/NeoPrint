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
}

/// <summary>A job printed: its owner hears it (plan section 8.4, <c>JobCompleted</c>).</summary>
public sealed record NotifyJobCompletedCommand(Guid JobId) : IRequest;

/// <summary>Money went back to a registered user (plan section 8.4, <c>RefundCompleted</c>).</summary>
public sealed record NotifyRefundCompletedCommand(Guid RefundId) : IRequest;

/// <summary>
/// What each event tells the user. Each sends one <see cref="NotifyUserCommand"/>
/// with a key that makes a repeat of the event a no-op.
/// </summary>
public sealed class NotificationTriggers :
    IRequestHandler<NotifyJobCompletedCommand>,
    IRequestHandler<NotifyRefundCompletedCommand>
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
            UserMessage.Of("notifications.scheduleAutoCancelled.body", ("kiosk", kiosk), ("amount", schedule.Price.Total.Amount)),
            NotificationLinks.Schedule(schedule.Id),
            $"ScheduleAutoCancelled:{schedule.Id}");
    }

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
                        ("amount", refund.Amount.Amount),
                        ("destination", refund.Destination.ToString())),
                    NotificationLinks.Wallet,
                    $"RefundCompleted:{refund.Id}"),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
