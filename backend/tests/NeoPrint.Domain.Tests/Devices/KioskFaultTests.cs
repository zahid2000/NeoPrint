using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// A kiosk whose printer cannot print (plan section 9.6, scenario A): Paused
/// while that lasts, unless something stronger holds it - and never Online
/// again by a side door while the printer is still down.
/// </summary>
public sealed class KioskFaultTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Device = ActorContext.Kiosk(Guid.NewGuid(), correlationId: "corr-fault");

    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-fault");

    private static Kiosk OnlineKiosk()
    {
        var kiosk = new Kiosk { Code = "KSK-0001", SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" } };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.7.0", Admin, Now);
        return kiosk;
    }

    [Fact]
    public void A_fault_pauses_an_online_kiosk_and_its_end_brings_it_back()
    {
        var kiosk = OnlineKiosk();

        kiosk.DeclareFault("printer.outOfPaper", Device, Now.AddMinutes(1)).Should().BeTrue("the outage begins now");

        kiosk.Status.Should().Be(KioskStatus.Paused);
        kiosk.FaultSince.Should().Be(Now.AddMinutes(1));
        kiosk.FaultCode.Should().Be("printer.outOfPaper");
        kiosk.WorkingStatus.Should().Be(KioskStatus.Paused);
        kiosk.PendingTransitions[^1].Reason.Should().Be("printer.outOfPaper", "the history says why it stopped");

        kiosk.ClearFault(Device, Now.AddMinutes(9)).Should().BeTrue();

        kiosk.Status.Should().Be(KioskStatus.Online);
        kiosk.FaultSince.Should().BeNull();
        kiosk.FaultCode.Should().BeNull();
        kiosk.PrinterDownSince.Should().BeNull();
        kiosk.PendingTransitions[^1].Reason.Should().Be("kiosks.reason.printerRecovered");
    }

    [Fact]
    public void A_second_report_of_the_same_outage_begins_nothing_but_keeps_the_latest_problem()
    {
        var kiosk = OnlineKiosk();

        kiosk.DeclareFault("printer.offline", Device, Now.AddMinutes(1));
        var transitions = kiosk.PendingTransitions.Count;

        kiosk.DeclareFault("printer.paperJam", Device, Now.AddMinutes(2)).Should().BeFalse("one outage, however often it is reported");

        kiosk.FaultSince.Should().Be(Now.AddMinutes(1));
        kiosk.FaultCode.Should().Be("printer.paperJam");
        kiosk.PendingTransitions.Should().HaveCount(transitions);
    }

    [Fact]
    public void Clearing_a_fault_that_was_never_declared_changes_nothing()
    {
        var kiosk = OnlineKiosk();
        var transitions = kiosk.PendingTransitions.Count;

        kiosk.NotePrinterDown(Now.AddSeconds(15));
        kiosk.ClearFault(Device, Now.AddSeconds(30)).Should().BeFalse();

        kiosk.Status.Should().Be(KioskStatus.Online);
        kiosk.PrinterDownSince.Should().BeNull("the bad reading was a blip");
        kiosk.PendingTransitions.Should().HaveCount(transitions);
    }

    [Fact]
    public void The_first_bad_reading_is_remembered_and_later_ones_do_not_move_it()
    {
        var kiosk = OnlineKiosk();

        kiosk.NotePrinterDown(Now.AddSeconds(15)).Should().Be(Now.AddSeconds(15));
        kiosk.NotePrinterDown(Now.AddSeconds(30)).Should().Be(Now.AddSeconds(15));

        kiosk.Status.Should().Be(KioskStatus.Online, "a reading is not an outage yet");

        kiosk.NotePrinterUp();
        kiosk.NotePrinterDown(Now.AddSeconds(60)).Should().Be(Now.AddSeconds(60), "a good reading in between starts the count again");
    }

    [Fact]
    public void An_administrators_pause_outlives_the_fault_and_its_end()
    {
        var kiosk = OnlineKiosk();

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(1));
        kiosk.DeclareFault("printer.paperJam", Device, Now.AddMinutes(2)).Should().BeTrue();

        kiosk.Status.Should().Be(KioskStatus.Maintenance, "the pause is the administrator's");
        kiosk.FaultSince.Should().NotBeNull("the fault is remembered all the same");

        kiosk.ClearFault(Device, Now.AddMinutes(3));

        kiosk.Status.Should().Be(KioskStatus.Maintenance);
    }

    [Fact]
    public void Resuming_a_kiosk_whose_printer_is_still_down_leaves_it_paused_not_online()
    {
        var kiosk = OnlineKiosk();

        kiosk.PauseByAdmin(Admin, Now.AddMinutes(1));
        kiosk.DeclareFault("printer.outOfPaper", Device, Now.AddMinutes(2));

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(10)).Should().BeTrue();

        kiosk.Status.Should().Be(KioskStatus.Paused, "an order taken now could not be printed");

        kiosk.ClearFault(Device, Now.AddMinutes(11));

        kiosk.Status.Should().Be(KioskStatus.Online);
    }

    [Fact]
    public void A_fault_declared_before_an_administrators_pause_is_still_there_after_it()
    {
        var kiosk = OnlineKiosk();

        kiosk.DeclareFault("printer.offline", Device, Now.AddMinutes(1));
        kiosk.PauseByAdmin(Admin, Now.AddMinutes(2));

        kiosk.Status.Should().Be(KioskStatus.Maintenance);

        kiosk.ResumeByAdmin(Admin, Now.AddMinutes(3));

        kiosk.Status.Should().Be(KioskStatus.Paused);
    }

    [Fact]
    public void An_offline_kiosk_keeps_its_fault_and_comes_back_paused()
    {
        var kiosk = OnlineKiosk();

        kiosk.DeclareFault("printer.paperJam", Device, Now.AddMinutes(1));
        kiosk.MoveTo(KioskStatus.Offline, ActorContext.System("kiosks.reason.heartbeatLost"), Now.AddMinutes(5));

        // What the heartbeat does when the kiosk calls in again.
        kiosk.MoveTo(kiosk.WorkingStatus, Device, Now.AddMinutes(30));

        kiosk.Status.Should().Be(KioskStatus.Paused, "back in touch is not the same as able to print");
    }

    [Fact]
    public void Leaving_safe_mode_with_the_printer_down_goes_to_paused()
    {
        var kiosk = OnlineKiosk();

        kiosk.EnterSafeMode(Device, Now.AddMinutes(1));
        kiosk.Status.Should().Be(KioskStatus.Maintenance);

        kiosk.DeclareFault("printer.offline", Device, Now.AddMinutes(2));
        kiosk.LeaveSafeMode(Device, Now.AddMinutes(3));

        kiosk.Status.Should().Be(KioskStatus.Paused);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void A_fault_needs_a_reason(string? code)
    {
        var kiosk = OnlineKiosk();

        var declare = () => kiosk.DeclareFault(code!, Device, Now);

        declare.Should().Throw<ArgumentException>();
        kiosk.Status.Should().Be(KioskStatus.Online);
    }
}
