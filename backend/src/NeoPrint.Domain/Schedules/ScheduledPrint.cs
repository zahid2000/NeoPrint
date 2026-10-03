using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Domain.Schedules;

/// <summary>Where a scheduled print is (docs/STATE_MACHINES.md, "ScheduledPrint").</summary>
public enum ScheduledPrintStatus
{
    /// <summary>Made, its job not paid yet; nothing happens until it is.</summary>
    PendingPayment = 0,

    Scheduled = 1,

    /// <summary>The time came: the app asks "print now or change the time?".</summary>
    AwaitingUser = 2,

    /// <summary>Into the kiosk's queue; the job tells the rest.</summary>
    Sent = 3,

    Cancelled = 4,

    /// <summary>Nobody answered within <c>Schedule.AutoCancelHours</c>; cancelled and refunded.</summary>
    Expired = 5,
}

/// <summary>
/// A print paid in the app for a chosen kiosk and time (plan section 9.5,
/// <c>scheduled_prints</c>; <c>docs/DECISIONS.md</c> T-145).
/// </summary>
/// <remarks>
/// The order itself is a <see cref="PrintJob"/> made at the same moment,
/// <see cref="PrintJobSource.Scheduled"/> and held for its owner: the payment,
/// the refund and the fiscal receipts are that job's, as for any other order.
/// This document adds only the time and what the user was asked.
/// </remarks>
public sealed class ScheduledPrint : Entity
{
    public Guid UserId { get; private set; }

    public Guid KioskId { get; private set; }

    public Guid FileId { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>The payment opened with it, the one the app pays.</summary>
    public Guid PaymentId { get; private set; }

    /// <summary>When its job was paid; a schedule never paid is left off the user's list once closed.</summary>
    public DateTime? PaidAt { get; private set; }

    public PrintOptions Options { get; private set; } = PrintOptions.Default;

    /// <summary>The job's price, kept here for the list.</summary>
    public PriceSnapshot Price { get; private set; } = null!;

    public DateTime ScheduledAt { get; private set; }

    public ScheduledPrintStatus Status { get; private set; }

    /// <summary>When the reminder went out for the current time; cleared when the time changes.</summary>
    public DateTime? RemindedAt { get; private set; }

    /// <summary>When it became <see cref="ScheduledPrintStatus.AwaitingUser"/>; the auto-cancel counts from here.</summary>
    public DateTime? AwaitingSince { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    public bool IsActive => Status is ScheduledPrintStatus.Scheduled or ScheduledPrintStatus.AwaitingUser;

    /// <summary>The transitions it may make (docs/STATE_MACHINES.md, "ScheduledPrint").</summary>
    public static readonly StateMachine<ScheduledPrintStatus> Transitions =
        new StateMachine<ScheduledPrintStatus>(nameof(ScheduledPrint))
            .Allow(ScheduledPrintStatus.PendingPayment, ScheduledPrintStatus.Scheduled, ScheduledPrintStatus.Cancelled)
            .Allow(
                ScheduledPrintStatus.Scheduled,
                ScheduledPrintStatus.AwaitingUser,
                ScheduledPrintStatus.Sent,
                ScheduledPrintStatus.Cancelled)
            .Allow(
                ScheduledPrintStatus.AwaitingUser,
                ScheduledPrintStatus.Scheduled,
                ScheduledPrintStatus.Sent,
                ScheduledPrintStatus.Cancelled,
                ScheduledPrintStatus.Expired);

    public static ScheduledPrint Create(
        Guid userId, Guid kioskId, Guid fileId, PrintJob job, Guid paymentId, DateTime scheduledAt, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new ScheduledPrint
        {
            UserId = userId,
            KioskId = kioskId,
            FileId = fileId,
            JobId = job.Id,
            PaymentId = paymentId,
            Options = job.Options,
            Price = job.Price,
            ScheduledAt = scheduledAt,
            Status = ScheduledPrintStatus.PendingPayment,
            CreatedAt = now,
        };
    }

    /// <summary>Its job was paid: it now waits for its time. A repeat is a no-op.</summary>
    public void Confirm(ActorContext actor, DateTime now)
    {
        if (Status != ScheduledPrintStatus.PendingPayment)
        {
            return;
        }

        Move(ScheduledPrintStatus.Scheduled, actor, now);
        PaidAt = now;
    }

    /// <summary>The reminder for the current time went out.</summary>
    public void NoteReminded(DateTime now) => RemindedAt ??= now;

    /// <summary>The time came (plan section 9.5, step 2).</summary>
    public void BecomeDue(ActorContext actor, DateTime now)
    {
        if (Status == ScheduledPrintStatus.AwaitingUser)
        {
            return;
        }

        Move(ScheduledPrintStatus.AwaitingUser, actor, now);
        AwaitingSince = now;
    }

    /// <summary>"Vaxtı dəyiş" (plan section 9.5, step 4); the caller checks the new time against the rules.</summary>
    /// <exception cref="DomainException">It is no longer waiting for its time.</exception>
    public void Reschedule(DateTime scheduledAt, ActorContext actor, DateTime now)
    {
        if (!IsActive)
        {
            throw new DomainException("schedules.notActive");
        }

        Move(ScheduledPrintStatus.Scheduled, actor, now);
        ScheduledAt = scheduledAt;
        RemindedAt = null;
        AwaitingSince = null;
    }

    /// <summary>"Çapa ver": its job went into the kiosk's queue (plan section 9.5, step 3). A repeat is a no-op.</summary>
    public void MarkSent(ActorContext actor, DateTime now)
    {
        if (Status == ScheduledPrintStatus.Sent)
        {
            return;
        }

        if (!IsActive)
        {
            throw new DomainException("schedules.notActive");
        }

        Move(ScheduledPrintStatus.Sent, actor, now);
        ClosedAt = now;
    }

    /// <summary>Cancelled by its owner, or never paid. A repeat is a no-op.</summary>
    public void Cancel(ActorContext actor, DateTime now)
    {
        if (Status == ScheduledPrintStatus.Cancelled)
        {
            return;
        }

        if (Status is not (ScheduledPrintStatus.PendingPayment or ScheduledPrintStatus.Scheduled or ScheduledPrintStatus.AwaitingUser))
        {
            throw new DomainException("schedules.notActive");
        }

        Move(ScheduledPrintStatus.Cancelled, actor, now);
        ClosedAt = now;
    }

    /// <summary>Nobody answered in time (<c>Schedule.AutoCancelHours</c>). A repeat is a no-op.</summary>
    public void Expire(ActorContext actor, DateTime now)
    {
        if (Status == ScheduledPrintStatus.Expired)
        {
            return;
        }

        Move(ScheduledPrintStatus.Expired, actor, now);
        ClosedAt = now;
    }

    private void Move(ScheduledPrintStatus next, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, next, actor, now));
        Status = next;
    }
}
