using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>What keeps an update waiting, as the panel is told (T-361).</summary>
public sealed class UpdateBlockerTests
{
    [Fact]
    public void A_resting_screen_with_nothing_on_it_is_idle()
    {
        var model = new MainWindowViewModel { Screen = KioskScreen.Home };

        model.UpdateBlocker.Should().BeNull();
        model.IsIdleForUpdate.Should().BeTrue();
    }

    [Theory]
    [InlineData(KioskScreen.Admin, "updates.waiting.admin")]
    [InlineData(KioskScreen.Flow, "updates.waiting.customer")]
    [InlineData(KioskScreen.Starting, "updates.waiting.screen")]
    public void A_screen_in_use_is_said_first(KioskScreen screen, string reason)
    {
        var model = new MainWindowViewModel { Screen = screen };
        model.SessionOpened(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(10));

        model.UpdateBlocker.Should().Be(reason);
        model.IsIdleForUpdate.Should().BeFalse();
    }

    [Fact]
    public void A_phone_session_then_a_job_on_the_printer_then_waiting_work_keep_it_busy()
    {
        var model = new MainWindowViewModel { Screen = KioskScreen.OutOfService };
        var session = Guid.NewGuid();
        model.SessionOpened(session, DateTime.UtcNow.AddMinutes(10));
        model.SetQueue([new QueueEntry(1, "361001", string.Empty, 7, IsPrinting: true) { Sides = 7 }]);

        model.UpdateBlocker.Should().Be("updates.waiting.phone");

        model.SessionClosed(session);
        model.UpdateBlocker.Should().Be("updates.waiting.printing", "the paper ran out in the middle of the job");

        model.SetQueue([new QueueEntry(1, "361001", string.Empty, 7, IsPrinting: false) { Sides = 7 }]);
        model.Screen = KioskScreen.Home;
        model.UpdateBlocker.Should().Be("updates.waiting.queue");
    }
}
