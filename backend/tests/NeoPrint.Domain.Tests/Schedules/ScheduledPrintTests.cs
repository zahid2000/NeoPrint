using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Schedules;

namespace NeoPrint.Domain.Tests.Schedules;

/// <summary>The scheduled print state machine (docs/STATE_MACHINES.md, "ScheduledPrint").</summary>
public sealed class ScheduledPrintTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext User = ActorContext.User(Guid.NewGuid());
    private static readonly ActorContext System = ActorContext.System();

    private static ScheduledPrint NewSchedule()
    {
        var profile = new PricingProfile
        {
            Name = "Standart",
            IsDefault = true,
            BwPricePerSide = Money.FromAzn(0.10m),
            ColorPricePerSide = Money.FromAzn(0.30m),
            MinOrder = Money.FromAzn(0.20m),
            PaperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m },
        };

        var options = PrintOptions.Default;
        var job = PrintJob.Create("123456", Guid.NewGuid(), PrintJobSource.Scheduled, 3, options,
            PriceSnapshot.Take(profile, options.ToSpec(3), Now), Now, userId: Guid.NewGuid(), heldForUser: true);

        return ScheduledPrint.Create(job.UserId!.Value, job.KioskId, Guid.NewGuid(), job, Now.AddHours(2), Now);
    }

    private static ScheduledPrint Paid()
    {
        var schedule = NewSchedule();
        schedule.Confirm(Guid.NewGuid(), System, Now);
        return schedule;
    }

    [Fact]
    public void It_waits_for_its_payment_then_for_its_time_and_asks_the_user()
    {
        var schedule = NewSchedule();
        schedule.Status.Should().Be(ScheduledPrintStatus.PendingPayment);
        schedule.Price.Total.Amount.Should().Be(0.30m);
        schedule.IsActive.Should().BeFalse();

        var paymentId = Guid.NewGuid();
        schedule.Confirm(paymentId, System, Now);
        schedule.Confirm(Guid.NewGuid(), System, Now);
        schedule.Status.Should().Be(ScheduledPrintStatus.Scheduled);
        schedule.PaymentId.Should().Be(paymentId, "a repeated payment event changes nothing");

        schedule.BecomeDue(System, Now.AddHours(2));
        schedule.BecomeDue(System, Now.AddHours(3));
        schedule.Status.Should().Be(ScheduledPrintStatus.AwaitingUser);
        schedule.AwaitingSince.Should().Be(Now.AddHours(2));

        schedule.MarkSent(User, Now.AddHours(2));
        schedule.MarkSent(User, Now.AddHours(2));
        schedule.Status.Should().Be(ScheduledPrintStatus.Sent);
        schedule.DrainTransitions().Select(t => t.ToStatus)
            .Should().Equal(nameof(ScheduledPrintStatus.Scheduled), nameof(ScheduledPrintStatus.AwaitingUser), nameof(ScheduledPrintStatus.Sent));
    }

    [Fact]
    public void It_can_be_sent_before_its_time()
    {
        var schedule = Paid();

        schedule.MarkSent(User, Now);

        schedule.Status.Should().Be(ScheduledPrintStatus.Sent);
    }

    [Fact]
    public void A_new_time_starts_over_and_asks_again()
    {
        var schedule = Paid();
        schedule.NoteReminded(Now);
        schedule.BecomeDue(System, Now);

        schedule.Reschedule(Now.AddDays(1), User, Now);

        schedule.Status.Should().Be(ScheduledPrintStatus.Scheduled);
        schedule.ScheduledAt.Should().Be(Now.AddDays(1));
        schedule.RemindedAt.Should().BeNull();
        schedule.AwaitingSince.Should().BeNull();
    }

    [Fact]
    public void Only_a_due_one_expires_and_a_closed_one_moves_no_more()
    {
        var waiting = Paid();
        FluentActions.Invoking(() => waiting.Expire(System, Now)).Should().Throw<IllegalStateTransitionException>();

        waiting.BecomeDue(System, Now);
        waiting.Expire(System, Now);
        waiting.Expire(System, Now);
        waiting.Status.Should().Be(ScheduledPrintStatus.Expired);

        FluentActions.Invoking(() => waiting.Cancel(User, Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => waiting.MarkSent(User, Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => waiting.Reschedule(Now.AddDays(1), User, Now)).Should().Throw<DomainException>();

        var unpaid = NewSchedule();
        FluentActions.Invoking(() => unpaid.MarkSent(User, Now)).Should().Throw<DomainException>();
        unpaid.Cancel(User, Now);
        unpaid.Cancel(User, Now);
        unpaid.Status.Should().Be(ScheduledPrintStatus.Cancelled);
    }
}
