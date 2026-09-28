using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// A kiosk whose UI started in safe mode (plan section 7.12) stops looking
/// open, and comes back only from the Maintenance safe mode itself brought.
/// </summary>
public sealed class KioskSafeModeTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Device = ActorContext.Kiosk(Guid.NewGuid(), correlationId: "corr-safe");

    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-safe");

    private static Kiosk OnlineKiosk()
    {
        var kiosk = new Kiosk
        {
            Code = "KSK-0001",
            SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" },
        };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.3.0", Admin, Now);
        return kiosk;
    }

    [Fact]
    public void Safe_mode_takes_an_online_kiosk_to_maintenance_and_back()
    {
        var kiosk = OnlineKiosk();

        kiosk.EnterSafeMode(Device, Now.AddMinutes(1));

        kiosk.Status.Should().Be(KioskStatus.Maintenance);
        kiosk.SafeModeSince.Should().Be(Now.AddMinutes(1));
        kiosk.PendingTransitions[^1].Reason.Should().Be("kiosks.safeMode");

        kiosk.LeaveSafeMode(Device, Now.AddHours(1));

        kiosk.Status.Should().Be(KioskStatus.Online);
        kiosk.SafeModeSince.Should().BeNull();
        kiosk.PendingTransitions[^1].Reason.Should().Be("kiosks.safeModeCleared");
    }

    [Fact]
    public void Every_heartbeat_in_safe_mode_is_one_transition_not_many()
    {
        var kiosk = OnlineKiosk();

        kiosk.EnterSafeMode(Device, Now.AddMinutes(1));
        kiosk.EnterSafeMode(Device, Now.AddMinutes(2));
        kiosk.LeaveSafeMode(Device, Now.AddMinutes(3));
        kiosk.LeaveSafeMode(Device, Now.AddMinutes(4));

        kiosk.SafeModeSince.Should().BeNull();
        kiosk.PendingTransitions.Should().HaveCount(3, "bind, into maintenance, back online");
    }

    [Fact]
    public void An_administrators_maintenance_is_not_lifted_by_the_kiosk()
    {
        var kiosk = OnlineKiosk();
        kiosk.MoveTo(KioskStatus.Maintenance, Admin.Because("Printer service"), Now.AddMinutes(1));

        kiosk.EnterSafeMode(Device, Now.AddMinutes(2));
        kiosk.LeaveSafeMode(Device, Now.AddMinutes(3));

        kiosk.Status.Should().Be(KioskStatus.Maintenance, "somebody took it out of service on purpose");
        kiosk.SafeModeSince.Should().BeNull();
    }

    [Fact]
    public void A_paused_kiosk_stays_paused_and_still_carries_the_flag()
    {
        var kiosk = OnlineKiosk();
        kiosk.MoveTo(KioskStatus.Paused, Admin, Now.AddMinutes(1));

        kiosk.EnterSafeMode(Device, Now.AddMinutes(2));

        kiosk.Status.Should().Be(KioskStatus.Paused);
        kiosk.SafeModeSince.Should().Be(Now.AddMinutes(2));
    }
}
