using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// A kiosk's night sleep (T-448): the last heartbeat before it says when the
/// kiosk wakes, and the offline monitor leaves it alone until then - but never
/// for longer than a night, and never once it has called in again.
/// </summary>
public sealed class KioskSleepTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 23, 30, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    [Fact]
    public void A_kiosk_asleep_until_the_morning_is_asleep_until_then_and_the_grace_after()
    {
        var kiosk = new Kiosk();
        var morning = Now.AddHours(6.5);

        kiosk.ReportSleep(morning, Now);

        kiosk.SleepingUntil.Should().Be(morning);
        kiosk.IsAsleep(Now.AddHours(3), Grace).Should().BeTrue();
        kiosk.IsAsleep(morning.AddMinutes(9), Grace).Should().BeTrue();
        kiosk.IsAsleep(morning.AddMinutes(11), Grace).Should().BeFalse("a kiosk that did not wake is offline");
    }

    [Fact]
    public void Any_other_heartbeat_wakes_it()
    {
        var kiosk = new Kiosk();
        kiosk.ReportSleep(Now.AddHours(6), Now);

        kiosk.ReportSleep(null, Now.AddHours(6));

        kiosk.SleepingUntil.Should().BeNull();
        kiosk.IsAsleep(Now.AddHours(6), Grace).Should().BeFalse();
    }

    [Fact]
    public void A_time_in_the_past_or_beyond_a_night_is_not_taken()
    {
        var kiosk = new Kiosk();

        kiosk.ReportSleep(Now.AddMinutes(-1), Now);
        kiosk.SleepingUntil.Should().BeNull();

        kiosk.ReportSleep(Now + Kiosk.MaxSleep + TimeSpan.FromMinutes(1), Now);
        kiosk.SleepingUntil.Should().BeNull("a kiosk cannot hide from the offline monitor for days");

        kiosk.ReportSleep(Now + Kiosk.MaxSleep, Now);
        kiosk.SleepingUntil.Should().Be(Now + Kiosk.MaxSleep);
    }
}
