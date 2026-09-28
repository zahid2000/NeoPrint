using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private KioskSession? _session;

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
        _session = new KioskSession(Services, _model);

        var window = new MainWindow { DataContext = _model };

        // Any touch restarts the idle clock (plan section 2.4, idle reset).
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => _session.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, (_, _) => _session.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);

        desktop.MainWindow = window;

        // Application-level lockdown. The operating system level is the
        // provisioning script's job (plan section 7.12).
        Services.GetRequiredService<IKioskLockdown>().Apply();

        desktop.ShutdownRequested += (_, _) => _stopping?.Cancel();

        // Enrollment runs in the background so the window paints immediately;
        // a kiosk that shows nothing for several seconds looks broken.
        _ = RunAsync(_model, _session, _stopping.Token);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Enrollment, then work, then enrollment again if the device is detached
    /// (plan section 7.11) - until the application stops.
    /// </summary>
    private async Task RunAsync(MainWindowViewModel model, KioskSession session, CancellationToken cancellationToken)
    {
        var logger = Services.GetRequiredService<ILogger<App>>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await RunEnrollmentLoopAsync(model, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                logger.LogInformation("Enrollment complete; the kiosk is activated.");

                if (!await session.RunAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "The kiosk's main loop stopped.");
        }
    }

    /// <summary>
    /// Keeps trying to advance the device's state until it is activated.
    /// </summary>
    /// <remarks>
    /// Wakes at once on the provisioning hub's messages (activated, revoked, a
    /// renewed code) and polls as well, because a kiosk left on the activation
    /// screen must still notice it has been activated if the SignalR message
    /// never arrives (plan section 7.11, docs/TASKS.md T-062).
    /// </remarks>
    /// <returns>True once the device is active; false when the application stops.</returns>
    private async Task<bool> RunEnrollmentLoopAsync(MainWindowViewModel model, CancellationToken cancellationToken)
    {
        var enrollment = Services.GetRequiredService<DeviceEnrollmentService>();
        var fingerprint = Services.GetRequiredService<IMachineFingerprintProvider>();
        var settings = Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KioskSettings>>().Value;
        var hub = Services.GetRequiredService<ProvisioningHubClient>();
        var logger = Services.GetRequiredService<ILogger<App>>();

        model.Activation.Fingerprint = fingerprint.Collect();

        using var wake = new SemaphoreSlim(0, int.MaxValue);
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Func<Task> poke = () =>
        {
            wake.Release();
            return Task.CompletedTask;
        };
        Func<string, DateTime, Task> codeRenewed = (_, _) => poke();

        hub.DeviceActivated += poke;
        hub.DeviceRevoked += poke;
        hub.PairingCodeRenewed += codeRenewed;
        hub.Reconnected += poke;

        var hubStarted = false;

        try
        {
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
                        return true;
                    }

                    // Registered and waiting: from here the backend can tell
                    // this device about itself directly.
                    if (!hubStarted && state.Lifecycle != KioskLifecycle.Unregistered)
                    {
                        hubStarted = true;
                        _ = StartQuietlyAsync(hub, logger, listening.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
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
                    await wake.WaitAsync(
                            TimeSpan.FromSeconds(Math.Max(3, settings.ActivationPollSeconds)), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }
        finally
        {
            hub.DeviceActivated -= poke;
            hub.DeviceRevoked -= poke;
            hub.PairingCodeRenewed -= codeRenewed;
            hub.Reconnected -= poke;

            await listening.CancelAsync().ConfigureAwait(false);
            await hub.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task StartQuietlyAsync(ProvisioningHubClient hub, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The poll still carries enrollment; the hub only makes it quicker.
            logger.LogWarning(exception, "The provisioning hub could not be started; relying on the poll.");
        }
    }
}
