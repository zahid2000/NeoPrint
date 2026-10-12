using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// An administrator's pause (plan section 7.8): theirs to set and theirs to
/// lift, whatever the kiosk's connection, printer or safe mode do meanwhile.
/// </summary>
public sealed class KioskAdminPauseTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Device = ActorContext.Kiosk(Guid.NewGuid(), correlationId: "corr-pause");

    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-pause");

    private static Kiosk OnlineKiosk(bool printer = true)
    {
        var kiosk = new Kiosk
        {
            Code = "KSK-0001",
            SelectedPrinter = printer ? new SelectedPrinter { Name = "HP LaserJet" } : null,

            // Stated, not left to its default - which is the real clock, and
            // the test's "now" is a fixed day: left alone, "changed after it
            // was configured" stopped holding the moment that day passed (T-240).
            ConfigurationChangedAt = Now.AddDays(-1),
        };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.7.0", Admin, Now);
        return kiosk;
    }

    [Fact]
    public void Pausing_takes_an_online_kiosk_to_maintenance_and_resuming_brings_it_back()
    {
        var kiosk = OnlineKiosk();
        var configuredAt = kiosk.ConfigurationChangedAt;

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(1)).Should().BeTrue();

        kiosk.Status.Should().Be(KioskStatus.Maintenance);
        kiosk.PausedByAdminSince.Should().Be(Now.AddMinutes(1));
        kiosk.WorkingStatus.Should().Be(KioskStatus.Maintenance);
        kiosk.ConfigurationChangedAt.Should().BeAfter(configuredAt, "the kiosk must fetch its configuration again");
        kiosk.PendingTransitions[^1].Reason.Should().Be("kiosks.reason.pausedByAdmin");

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(30)).Should().BeTrue();

        kiosk.Status.Should().Be(KioskStatus.Online);
        kiosk.PausedByAdminSince.Should().BeNull();
        kiosk.PendingTransitions[^1].Reason.Should().Be("kiosks.reason.resumedByAdmin");
    }

    [Fact]
    public void Pausing_or_resuming_twice_changes_nothing_the_second_time()
    {
        var kiosk = OnlineKiosk();

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(1));
        var transitions = kiosk.PendingTransitions.Count;

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(2)).Should().BeFalse();
        kiosk.PausedByAdminSince.Should().Be(Now.AddMinutes(1));
        kiosk.PendingTransitions.Should().HaveCount(transitions);

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(3));
        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(4)).Should().BeFalse();
        kiosk.Status.Should().Be(KioskStatus.Online);
    }

    [Theory]
    [InlineData(KioskStatus.Offline)]
    [InlineData(KioskStatus.Paused)]
    public void A_kiosk_that_is_offline_or_stopped_by_its_printer_is_paused_all_the_same(KioskStatus from)
    {
        var kiosk = OnlineKiosk();
        kiosk.MoveTo(from, ActorContext.System("test"), Now.AddMinutes(1));

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(2));

        kiosk.Status.Should().Be(KioskStatus.Maintenance);
    }

    [Fact]
    public void A_kiosk_waiting_for_a_printer_keeps_waiting_and_goes_into_maintenance_once_it_has_one()
    {
        var kiosk = OnlineKiosk(printer: false);
        kiosk.MoveTo(KioskStatus.NotConfigured, ActorContext.System("test"), Now.AddMinutes(1));

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(2));

        kiosk.Status.Should().Be(KioskStatus.NotConfigured, "it could take no orders anyway");
        kiosk.WorkingStatus.Should().Be(KioskStatus.Maintenance, "what it becomes when its test page prints");

        kiosk.SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" };
        kiosk.MoveTo(kiosk.WorkingStatus, Device.Because("kiosks.reason.testPrintPassed"), Now.AddMinutes(5));
        kiosk.Status.Should().Be(KioskStatus.Maintenance);

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(9));
        kiosk.Status.Should().Be(KioskStatus.Online);
    }

    [Fact]
    public void A_pause_set_during_safe_mode_outlives_it_and_safe_mode_outlives_a_resume()
    {
        var kiosk = OnlineKiosk();

        // Safe mode first, then the administrator's pause on top.
        kiosk.EnterSafeMode(Device, Now.AddMinutes(1));
        kiosk.PauseByAdmin(Admin, Now.AddMinutes(2));

        kiosk.LeaveSafeMode(Device, Now.AddMinutes(10));
        kiosk.Status.Should().Be(KioskStatus.Maintenance, "leaving safe mode does not lift the administrator's pause");

        // And the other way round: resumed while the kiosk is still in safe mode.
        kiosk.EnterSafeMode(Device, Now.AddMinutes(11));
        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(12));
        kiosk.Status.Should().Be(KioskStatus.Maintenance, "safe mode still takes no orders");

        kiosk.LeaveSafeMode(Device, Now.AddMinutes(20));
        kiosk.Status.Should().Be(KioskStatus.Online);
    }

    [Fact]
    public void A_resumed_kiosk_without_a_printer_goes_back_to_waiting_for_one()
    {
        var kiosk = OnlineKiosk();
        kiosk.PauseByAdmin(Admin, Now.AddMinutes(1));
        kiosk.SelectedPrinter = null;

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(2));

        kiosk.Status.Should().Be(KioskStatus.NotConfigured);
    }

    // -----------------------------------------------------------------------
    // The command itself
    // -----------------------------------------------------------------------

    [Fact]
    public void A_command_can_be_acknowledged_once_and_answered_afterwards()
    {
        var command = new KioskCommand { KioskId = Guid.NewGuid(), Type = KioskCommandType.RestartApp, CreatedAt = Now };

        command.IsOpen.Should().BeTrue();

        command.Acknowledge(Device, Now.AddSeconds(5));
        command.Status.Should().Be(KioskCommandStatus.Acknowledged);
        command.IsOpen.Should().BeTrue("the result is still owed");

        var transitions = command.PendingTransitions.Count;
        command.Acknowledge(Device, Now.AddSeconds(6));
        command.PendingTransitions.Should().HaveCount(transitions, "said twice, it is one acknowledgement");

        command.Complete(succeeded: true, new UserMessage("kioskCommands.restarted"), Device, Now.AddMinutes(1));
        command.Status.Should().Be(KioskCommandStatus.Succeeded);
        command.IsOpen.Should().BeFalse();

        command.Acknowledge(Device, Now.AddMinutes(2));
        command.Status.Should().Be(KioskCommandStatus.Succeeded, "an answered command is not reopened");
    }

    [Fact]
    public void An_acknowledged_command_nobody_finished_expires_after_a_day()
    {
        var command = new KioskCommand { KioskId = Guid.NewGuid(), Type = KioskCommandType.Reboot, CreatedAt = Now };
        command.Acknowledge(Device, Now.AddSeconds(5));

        command.IsOverdue(Now.AddHours(23)).Should().BeFalse();
        command.IsOverdue(Now.AddHours(25)).Should().BeTrue();

        command.Expire(Now.AddHours(25));
        command.Status.Should().Be(KioskCommandStatus.Expired);
    }
}
