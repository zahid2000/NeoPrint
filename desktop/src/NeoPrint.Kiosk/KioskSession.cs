using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk;

/// <summary>
/// An activated kiosk at work (plan sections 7.2–7.9): configuration, the
/// kiosk hub, the print queue, printer reports and commands, the flash drive
/// and the idle reset, all tied to the screen.
/// </summary>
/// <remarks>
/// Runs until it is cancelled or the device is detached; in the second case it
/// wipes the spool, the journal and the cached configuration before handing
/// back, so nothing of this kiosk is left for whoever gets the machine next
/// (plan section 7.11).
/// </remarks>
public sealed class KioskSession
{
    private static readonly TimeSpan DrivePoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrinterReportInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly MainWindowViewModel _model;
    private readonly ILogger<KioskSession> _logger;

    private DateTime _lastTouch = DateTime.UtcNow;

    public KioskSession(IServiceProvider services, MainWindowViewModel model)
    {
        _services = services;
        _model = model;
        _logger = services.GetRequiredService<ILogger<KioskSession>>();
    }

    /// <summary>Somebody touched the screen: the idle clock starts again.</summary>
    public void Touch() => _lastTouch = DateTime.UtcNow;

    /// <returns>True when the device was detached and enrollment has to start over.</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var runtime = _services.GetRequiredService<KioskRuntime>();
        var hub = _services.GetRequiredService<KioskHubClient>();
        var worker = _services.GetRequiredService<PrintQueueWorker>();
        var reporter = _services.GetRequiredService<PrinterReporter>();
        var commands = _services.GetRequiredService<CommandProcessor>();
        var orders = _services.GetRequiredService<KioskOrderService>();
        var drives = _services.GetRequiredService<IRemovableDriveWatcher>();
        var printers = _services.GetRequiredService<IPrinterService>();
        var api = _services.GetRequiredService<KioskApiClient>();

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = session.Token;

        // The services are singletons that outlive this session (a detached
        // kiosk can be activated again): every subscription is undone at the end.
        var unsubscribe = new List<Action>();
        var workerRun = Task.CompletedTask;
        var revoked = false;

        try
        {
            await OnUiAsync(() => SetUpFlow(orders, drives, unsubscribe)).ConfigureAwait(false);

            Action<KioskConfiguration> configurationChanged = configuration =>
                Dispatcher.UIThread.Post(() => _model.ApplyConfiguration(configuration));
            runtime.ConfigurationChanged += configurationChanged;
            unsubscribe.Add(() => runtime.ConfigurationChanged -= configurationChanged);

            try
            {
                await runtime.LoadConfigurationAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
            {
                // The backend is out of reach at start: the last configuration on
                // disk carries the kiosk while it is fresh enough (plan 7.7, Degraded).
                _logger.LogWarning(exception, "The configuration could not be loaded; trying the cached one.");

                if (!runtime.LoadCachedConfiguration())
                {
                    await OnUiAsync(() => _model.ApplyAvailability(runtime.Availability, hasConfiguration: false)).ConfigureAwait(false);
                }
            }

            // ---- the hub: the queue and commands the moment they happen
            Func<IReadOnlyList<QueueItem>, Task> queueUpdated = async items =>
            {
                await OnUiAsync(() => _model.SetQueue(Map(items))).ConfigureAwait(false);
                worker.Wake();
            };
            Func<Guid, Task> jobChanged = _ =>
            {
                worker.Wake();
                return Task.CompletedTask;
            };
            Func<Task> configChanged = () => RefreshConfigurationAsync(runtime, token);
            Func<Guid, string, Task> commandReceived = (id, type) => commands.HandleAsync(id, type, token);
            Func<Task> reconnected = async () =>
            {
                await CatchUpAsync(runtime, commands, api, token).ConfigureAwait(false);
                worker.Wake();
            };
            Action<PrinterActivity> activityChanged = activity =>
                Dispatcher.UIThread.Post(() => _model.SetPrinterProblem(activity.LastErrorCode));

            hub.QueueUpdated += queueUpdated;
            hub.JobAdded += jobChanged;
            hub.JobCancelled += jobChanged;
            hub.PrinterConfigChanged += configChanged;
            hub.KioskConfigChanged += configChanged;
            hub.CommandReceived += commandReceived;
            hub.Reconnected += reconnected;
            worker.ActivityChanged += activityChanged;

            unsubscribe.Add(() =>
            {
                hub.QueueUpdated -= queueUpdated;
                hub.JobAdded -= jobChanged;
                hub.JobCancelled -= jobChanged;
                hub.PrinterConfigChanged -= configChanged;
                hub.KioskConfigChanged -= configChanged;
                hub.CommandReceived -= commandReceived;
                hub.Reconnected -= reconnected;
                worker.ActivityChanged -= activityChanged;
            });

            _ = RunGuardedAsync("kiosk hub", () => hub.StartAsync(token), token);

            await TryAsync("printer report", () => reporter.ReportAsync(force: true, token)).ConfigureAwait(false);
            await CatchUpAsync(runtime, commands, api, token).ConfigureAwait(false);

            workerRun = RunGuardedAsync("print queue", () => worker.RunAsync(token), token);
            _ = RunGuardedAsync("flash drive", () => WatchDrivesAsync(drives, orders, token), token);
            _ = RunGuardedAsync("printer reports", () => ReportPrintersAsync(reporter, token), token);

            await HeartbeatLoopAsync(runtime, printers, token).ConfigureAwait(false);
        }
        catch (DeviceRevokedException)
        {
            revoked = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await hub.StopAsync().ConfigureAwait(false);

            // The worker finishes (or abandons) the page it is on before the
            // spool can be wiped under it.
            await Task.WhenAny(workerRun, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);

            foreach (var undo in unsubscribe)
            {
                undo();
            }
        }

        if (revoked)
        {
            await DetachAsync().ConfigureAwait(false);
        }

        return revoked;
    }

    private void SetUpFlow(KioskOrderService orders, IRemovableDriveWatcher drives, List<Action> unsubscribe)
    {
        var flow = new PrintFlowViewModel(orders, drives) { Language = _model.Language };

        flow.Finished += () =>
        {
            _model.Screen = _model.PrinterProblem is null ? KioskScreen.Home : KioskScreen.OutOfService;
        };

        _model.Flow = flow;
        _model.CanPrintFromFlash = orders.CanTakeOrders;

        Action printFromUsb = async () =>
        {
            Touch();
            var drive = (await drives.ListAsync().ConfigureAwait(true)).FirstOrDefault();

            if (drive is null)
            {
                flow.Reset();
            }
            else
            {
                await flow.StartFromDriveAsync(drive).ConfigureAwait(true);
            }

            _model.Screen = KioskScreen.Flow;
        };

        Action cancelByCode = () =>
        {
            Touch();
            flow.StartCancel();
            _model.Screen = KioskScreen.Flow;
        };

        _model.PrintFromUsbRequested += printFromUsb;
        _model.CancelByCodeRequested += cancelByCode;

        unsubscribe.Add(() =>
        {
            _model.PrintFromUsbRequested -= printFromUsb;
            _model.CancelByCodeRequested -= cancelByCode;
        });
    }

    private async Task HeartbeatLoopAsync(KioskRuntime runtime, IPrinterService printers, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var selected = runtime.Configuration?.SelectedPrinter;
            var state = PrinterState.NotFound;

            try
            {
                if (selected is not null)
                {
                    state = await printers.GetStateAsync(selected, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The printer's state could not be read.");
                state = PrinterState.Error;
            }

            var healthy = state is PrinterState.Online or PrinterState.TonerLow;

            await runtime.BeatAsync(healthy, cancellationToken).ConfigureAwait(false);

            await OnUiAsync(() =>
                {
                    _model.ApplyAvailability(runtime.Availability, runtime.Configuration is not null);

                    if (selected is not null)
                    {
                        _model.SetPrinterProblem(healthy ? null : PrinterErrors.For(state));
                    }

                    ResetIfIdle(runtime);
                })
                .ConfigureAwait(false);

            await Task.Delay(runtime.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Nobody touched the screen for <c>Kiosk.IdleResetSeconds</c>: an order
    /// in progress is dropped and the next person starts at home (plan 2.4).
    /// </summary>
    private void ResetIfIdle(KioskRuntime runtime)
    {
        var limit = TimeSpan.FromSeconds(Math.Max(30, runtime.Configuration?.IdleResetSeconds ?? 90));

        if (_model.Screen == KioskScreen.Flow
            && _model.Flow is { Stage: not FlowStage.Working } flow
            && DateTime.UtcNow - _lastTouch > limit)
        {
            _logger.LogInformation("Idle for {Seconds} s; back to the home screen.", limit.TotalSeconds);
            flow.Reset();
            _model.Screen = _model.PrinterProblem is null ? KioskScreen.Home : KioskScreen.OutOfService;
        }
    }

    /// <summary>A drive appears: the flash browser opens (from home); it goes: the unordered file is dropped.</summary>
    private async Task WatchDrivesAsync(IRemovableDriveWatcher drives, KioskOrderService orders, CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = await drives.ListAsync(cancellationToken).ConfigureAwait(false);
            var paths = current.Select(drive => drive.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var inserted = current.FirstOrDefault(drive => !known.Contains(drive.Path));
            var removed = known.Any(path => !paths.Contains(path));

            known = paths;

            if (removed)
            {
                await OnUiAsync(() => _model.Flow?.DriveRemoved()).ConfigureAwait(false);
            }

            if (inserted is not null && orders.CanTakeOrders)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        if (_model.Screen == KioskScreen.Home && _model.Flow is { } flow)
                        {
                            Touch();
                            await flow.StartFromDriveAsync(inserted).ConfigureAwait(true);
                            _model.Screen = KioskScreen.Flow;
                        }
                    })
                    .ConfigureAwait(false);
            }

            await Task.Delay(DrivePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReportPrintersAsync(PrinterReporter reporter, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PrinterReportInterval, cancellationToken).ConfigureAwait(false);
            await TryAsync("printer report", () => reporter.ReportAsync(force: false, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task CatchUpAsync(KioskRuntime runtime, CommandProcessor commands, KioskApiClient api, CancellationToken cancellationToken)
    {
        await TryAsync("configuration", () => runtime.RefreshAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("open commands", () => commands.CatchUpAsync(cancellationToken)).ConfigureAwait(false);
        await TryAsync("queue", async () =>
            {
                var queue = await api.GetQueueAsync(cancellationToken).ConfigureAwait(false);
                await OnUiAsync(() => _model.SetQueue(Map(queue))).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
    }

    private Task RefreshConfigurationAsync(KioskRuntime runtime, CancellationToken cancellationToken)
        => TryAsync("configuration", () => runtime.RefreshAsync(cancellationToken));

    private async Task DetachAsync()
    {
        _logger.LogWarning("This device was detached; wiping the spool and returning to activation.");

        _services.GetRequiredService<SpoolStore>().Wipe();
        _services.GetRequiredService<JobJournal>().Clear();
        _services.GetRequiredService<ConfigurationCache>().Clear();

        await OnUiAsync(() =>
            {
                _model.Flow?.Reset();
                _model.Flow = null;
                _model.SetQueue([]);
                _model.Screen = KioskScreen.Activation;
            })
            .ConfigureAwait(false);
    }

    private static IEnumerable<QueueEntry> Map(IReadOnlyList<QueueItem> items)
        => items.Select(item => new QueueEntry(item.Position, item.JobCode, item.UserInitial ?? string.Empty, item.Pages, item.Status == "Printing"));

    private async Task TryAsync(string what, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DeviceRevokedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not refresh the {What}; it is retried later.", what);
        }
    }

    private async Task RunGuardedAsync(string what, Func<Task> loop, CancellationToken cancellationToken)
    {
        try
        {
            await loop().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The {What} loop stopped.", what);
        }
    }

    private static Task OnUiAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
