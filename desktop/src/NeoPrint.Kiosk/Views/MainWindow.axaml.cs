using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Views;

/// <summary>
/// The kiosk window (plan section 7.1).
/// </summary>
/// <remarks>
/// Full screen, always on top, no decorations, and it refuses to close. On a
/// kiosk there is nowhere for the customer to go if this window is dismissed,
/// so closing it is only possible through the hidden admin menu
/// (<see cref="AllowClose"/>).
/// </remarks>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();

        _clock.Tick += OnClockTick;
    }

    private MainWindowViewModel? Model => DataContext as MainWindowViewModel;

    /// <summary>Set by the admin menu's actions just before the application goes.</summary>
    public bool AllowClose { get; set; }

    private void OnLogoPressed(object? sender, PointerPressedEventArgs e) => Model?.AdminTap(DateTime.UtcNow);

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _clock.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _clock.Stop();
        base.OnUnloaded(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Plan section 7.1: closing is blocked. Shell Launcher and cage would
        // restart the application anyway, but refusing here avoids the flicker
        // and makes the intent explicit.
        e.Cancel = !AllowClose;

        base.OnClosing(e);
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        model.Now = DateTime.Now;

        // Keeps the activation countdown moving without a second timer.
        model.Activation.Tick();
    }
}
