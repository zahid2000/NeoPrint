using NeoPrint.Application.Localization;
using NeoPrint.Application.Notifications;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Schedules;

namespace NeoPrint.Application.Tests.Notifications;

/// <summary>What the scheduled print notifications say (docs/DECISIONS.md T-146).</summary>
public sealed class NotificationTriggersTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>0.2 with no trailing zero, as a price read back from Mongo can be (found on the real stack).</summary>
    private static ScheduledPrint Schedule()
    {
        var profile = new PricingProfile
        {
            Name = "Standart",
            BwPricePerSide = Money.FromAzn(0.1m),
            MinOrder = Money.FromAzn(0.2m),
            PaperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m },
        };
        var options = PrintOptions.Default;
        var job = PrintJob.Create("123456", Guid.NewGuid(), PrintJobSource.Scheduled, 1, options,
            PriceSnapshot.Take(profile, options.ToSpec(1), Now), Now, userId: Guid.NewGuid(), heldForUser: true);

        return ScheduledPrint.Create(job.UserId!.Value, job.KioskId, Guid.NewGuid(), job, Guid.NewGuid(), Now.AddHours(2), Now);
    }

    [Fact]
    public void The_auto_cancel_names_the_kiosk_and_the_money_with_two_decimals_in_each_language()
    {
        var schedule = Schedule();
        var command = NotificationTriggers.ScheduleAutoCancelled(schedule, "KSK-9002");

        command.Type.Should().Be(NotificationType.ScheduleAutoCancelled);
        command.DedupKey.Should().Be($"ScheduleAutoCancelled:{schedule.Id}");
        command.Link.Should().Be($"neoprint://schedules/{schedule.Id}");
        Messages.Write(command.Body, "az").Should().Be("KSK-9002 kioskundakı çapa cavab verilmədi. 0,20 ₼ balansınıza qaytarıldı.");
        Messages.Write(command.Body, "en").Should().Contain("0.20 ₼");
        Messages.Write(command.Body, "ru").Should().Contain("0,20 ₼");
    }

    [Fact]
    public void A_new_time_is_a_new_reminder_the_same_time_is_one()
    {
        var schedule = Schedule();
        var first = NotificationTriggers.ScheduleDue(schedule, "KSK-9002");

        NotificationTriggers.ScheduleDue(schedule, "KSK-9002").DedupKey.Should().Be(first.DedupKey);
        schedule.Confirm(ActorContext.System(), Now);
        schedule.Reschedule(Now.AddHours(5), ActorContext.System(), Now);
        NotificationTriggers.ScheduleDue(schedule, "KSK-9002").DedupKey.Should().NotBe(first.DedupKey);
        Messages.Write(first.Title, "az").Should().Be("Planlanmış çapın vaxtı çatdı");
    }
}
