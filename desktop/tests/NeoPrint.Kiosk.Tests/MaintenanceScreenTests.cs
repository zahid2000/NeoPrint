using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk an administrator paused (plan section 7.8): the screen says
/// maintenance instead of offering to print, and goes back when the pause is
/// lifted - without getting in the way of a printer problem or of somebody
/// who is in the middle of an order.
/// </summary>
public sealed class MaintenanceScreenTests
{
    private static MainWindowViewModel Shell(KioskScreen screen = KioskScreen.Home)
        => new() { Language = "az", SelectedPrinterName = "HP LaserJet", Screen = screen };

    [Fact]
    public void A_paused_kiosk_says_maintenance_and_a_resumed_one_offers_to_print_again()
    {
        var shell = Shell();

        shell.SetMaintenance(true);

        shell.Screen.Should().Be(KioskScreen.OutOfService);
        shell.OutOfServiceTitle.Should().Be(Strings.Get("az", "maintenance.title"));
        shell.OutOfServiceBody.Should().Be(Strings.Get("az", "maintenance.body"));
        shell.OutOfServiceBody.Should().Contain("Növbədəki sifarişlər çap olunur", "the customer whose order is in the queue is told it is safe");

        shell.SetMaintenance(false);

        shell.Screen.Should().Be(KioskScreen.Home);
    }

    [Fact]
    public void Somebody_in_the_middle_of_an_order_is_not_thrown_out_of_it()
    {
        var shell = Shell(KioskScreen.Flow);

        shell.SetMaintenance(true);

        shell.Screen.Should().Be(KioskScreen.Flow);
        shell.IsMaintenance.Should().BeTrue();

        // When they are done - or walk away - the kiosk does not offer the
        // home screen to the next customer (found on the real stack: the
        // container kiosk sat in its flash browser and would have gone home).
        shell.RestingScreen.Should().Be(KioskScreen.OutOfService);

        shell.SetMaintenance(false);
        shell.RestingScreen.Should().Be(KioskScreen.Home);
    }

    [Fact]
    public void A_printer_problem_and_a_pause_each_keep_the_screen_until_both_are_gone()
    {
        var shell = Shell();

        shell.SetPrinterProblem("printer.paperJam");
        shell.SetMaintenance(true);

        // The jam is cleared, the pause is still there.
        shell.SetPrinterProblem(null);
        shell.Screen.Should().Be(KioskScreen.OutOfService);
        shell.OutOfServiceTitle.Should().Be(Strings.Get("az", "maintenance.title"));

        // And the other way round: resumed while the printer is still jammed.
        shell.SetPrinterProblem("printer.paperJam");
        shell.SetMaintenance(false);
        shell.Screen.Should().Be(KioskScreen.OutOfService);
        shell.OutOfServiceBody.Should().Contain(Strings.Get("az", "printer.paperJam"));

        shell.SetPrinterProblem(null);
        shell.Screen.Should().Be(KioskScreen.Home);
    }

    [Fact]
    public void Safe_mode_keeps_its_own_screen()
    {
        var shell = Shell();
        shell.EnterSafeMode();

        shell.SetMaintenance(true);

        shell.Screen.Should().Be(KioskScreen.SafeMode);
    }
}
