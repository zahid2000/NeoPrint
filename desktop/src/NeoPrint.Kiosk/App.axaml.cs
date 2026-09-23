using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;
using NeoPrint.Kiosk.Views;

namespace NeoPrint.Kiosk;

/// <summary>
/// The kiosk application.
/// </summary>
/// <remarks>
/// On start it works out what this installation is (plan sections 7.11, 7.12):
/// it shows the starting screen, generates a device key if it has none,
/// registers, and then either shows the activation code or moves on to the
/// kiosk's own configuration.
/// </remarks>
public sealed partial class App : Application
{
    private IServiceProvider? _services;
    private MainWindowViewModel? _model;
    private CancellationTokenSource? _stopping;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Set by <c>Program</c> before the application runs.</summary>
    public IServiceProvider Services
    {
        get => _services ?? throw new InvalidOperationException("The service provider was not set.");
        set => _services = value;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _stopping = new CancellationTokenSource();
        _model = new MainWindowViewModel();

        desktop.MainWindow = new MainWindow { DataContext = _model };

        // Application-level lockdown. The operating system level is the
        // provisioning script's job (plan section 7.12).
        Services.GetRequiredService<IKioskLockdown>().Apply();

        desktop.ShutdownRequested += (_, _) => _stopping?.Cancel();

        // Enrollment runs in the background so the window paints immediately;
        // a kiosk that shows nothing for several seconds looks broken.
        _ = RunEnrollmentLoopAsync(_model, _stopping.Token);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Keeps trying to advance the device's state until it is activated.
    /// </summary>
    /// <remarks>
    /// Runs on a poll as well as reacting to the real-time notification,
    /// because a kiosk left on the activation screen must still notice it has
    /// been activated if the SignalR message never arrives (plan section 7.11).
    /// </remarks>
    private async Task RunEnrollmentLoopAsync(MainWindowViewModel model, CancellationToken cancellationToken)
    {
        var enrollment = Services.GetRequiredService<DeviceEnrollmentService>();
        var fingerprint = Services.GetRequiredService<IMachineFingerprintProvider>();
        var settings = Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KioskSettings>>().Value;
        var logger = Services.GetRequiredService<ILogger<App>>();

        model.Activation.Fingerprint = fingerprint.Collect();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await enrollment.AdvanceAsync(cancellationToken).ConfigureAwait(false);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    model.Activation.IsBackendReachable = true;
                    model.Apply(state);
                });

                if (state.Lifecycle == KioskLifecycle.Active)
                {
                    // Loading the kiosk's own configuration, the heartbeat and
                    // the real-time connection land in step 3.
                    logger.LogInformation("Enrollment complete; the kiosk is activated.");
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // The backend being unreachable is an expected state on a kiosk
                // that was just plugged in: report it on screen and keep trying.
                logger.LogWarning(exception, "Advancing enrollment failed; retrying.");

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                    () => model.Activation.IsBackendReachable = false);
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(3, settings.ActivationPollSeconds)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
