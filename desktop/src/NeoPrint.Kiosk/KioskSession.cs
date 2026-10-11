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
    private static readonly TimeSpan AnalyticsInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UploadRetryInterval = TimeSpan.FromMinutes(2);

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
            // Safe mode (plan section 7.12) runs the least that tells the
            // backend where the kiosk stands: configuration, heartbeat and the
            // queue on screen. No orders, no flash drive, no printing, no
            // commands - whichever of them was crashing the UI stays off.
            var safeMode = runtime.SafeMode;

            if (!safeMode)
            {
                await OnUiAsync(() => SetUpFlow(orders, drives, unsubscribe)).ConfigureAwait(false);
            }

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
            Func<Guid, string, Task> commandReceived = (id, type) => safeMode ? Task.CompletedTask : commands.HandleAsync(id, type, token);
            Func<Task> reconnected = async () =>
            {
                await CatchUpAsync(runtime, commands, api, token).ConfigureAwait(false);
                worker.Wake();
            };
            // A payment this kiosk opened went through: the order may print, and
            // the payment screen moves on without waiting for its next poll.
            Func<Guid, Guid, Task> paymentConfirmed = async (paymentId, jobId) =>
            {
                orders.MarkPaid(jobId);
                await OnUiAsync(() => _ = _model.Flow?.PaymentConfirmed(paymentId)).ConfigureAwait(false);
            };
            // A phone joined or left (plan section 9.3): the header says so, never who.
            Func<Guid, DateTime, Task> sessionOpened = (sessionId, expiresAt) => OnUiAsync(() => _model.SessionOpened(sessionId, expiresAt));
            Func<Guid, string, Task> sessionClosed = (sessionId, _) => OnUiAsync(() => _model.SessionClosed(sessionId));
            var counters = _services.GetRequiredService<TelemetryCounters>();
            Action<PrinterActivity> activityChanged = activity =>
            {
                counters.Printing(activity.JobCode);
                Dispatcher.UIThread.Post(() => _model.SetPrinterProblem(activity.LastErrorCode));
            };

            // How each job ended, for the pilot report (plan section 2.17);
            // the order's own visit is over by then, so it goes under the job.
            var analytics = _services.GetRequiredService<IKioskAnalytics>();
            Action<JournalEntry> jobFinished = entry =>
            {
                counters.JobFinished(entry.PrintedSheets);
                analytics.Track(
                    entry.State == JournalState.Printed ? "print_completed" : "print_failed",
                    new Dictionary<string, string?>
                    {
                        ["sheets"] = entry.PrintedSheets.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["error"] = entry.ErrorCode,
                        ["jobCode"] = entry.JobCode,
                    },
                    sessionId: $"job-{entry.JobCode}");

                if (entry.State == JournalState.Interrupted)
                {
                    // Scenario A (plan section 2.5): a bank-paid order the printer
                    // stopped is refunded by the backend; the screen says so.
                    _ = RunGuardedAsync("refund notice", () => ShowRefundNoticeAsync(orders, entry.JobId, token), token);
                }
            };

            hub.QueueUpdated += queueUpdated;
            hub.JobAdded += jobChanged;
            hub.JobCancelled += jobChanged;
            hub.PrinterConfigChanged += configChanged;
            hub.KioskConfigChanged += configChanged;
            hub.CommandReceived += commandReceived;
            hub.Reconnected += reconnected;
            hub.PaymentConfirmed += paymentConfirmed;
            hub.SessionOpened += sessionOpened;
            hub.SessionClosed += sessionClosed;
            worker.ActivityChanged += activityChanged;
            worker.JobFinished += jobFinished;

            unsubscribe.Add(() =>
            {
                hub.QueueUpdated -= queueUpdated;
                hub.JobAdded -= jobChanged;
                hub.JobCancelled -= jobChanged;
                hub.PrinterConfigChanged -= configChanged;
                hub.KioskConfigChanged -= configChanged;
                hub.CommandReceived -= commandReceived;
                hub.Reconnected -= reconnected;
                hub.PaymentConfirmed -= paymentConfirmed;
                hub.SessionOpened -= sessionOpened;
                hub.SessionClosed -= sessionClosed;
                worker.ActivityChanged -= activityChanged;
                worker.JobFinished -= jobFinished;
            });

            _ = RunGuardedAsync("kiosk hub", () => hub.StartAsync(token), token);

            await TryAsync("printer report", () => reporter.ReportAsync(force: true, token)).ConfigureAwait(false);
            await CatchUpAsync(runtime, commands, api, token).ConfigureAwait(false);

            if (!safeMode)
            {
                workerRun = RunGuardedAsync("print queue", () => worker.RunAsync(token), token);
                _ = RunGuardedAsync("flash drive", () => WatchDrivesAsync(drives, orders, token), token);
            }

            _ = RunGuardedAsync("printer reports", () => ReportPrintersAsync(reporter, token), token);
            _ = RunGuardedAsync("analytics", () => SendAnalyticsAsync(api, token), token);
            _ = RunGuardedAsync("flash uploads", () => SendUploadsAsync(orders, token), token);
            _ = RunGuardedAsync("telemetry", () => SendTelemetryAsync(runtime, hub, commands, drives, api, token), token);
            _ = RunGuardedAsync("printer telemetry", () => SendPrinterTelemetryAsync(runtime, reporter, api, token), token);

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

    /// <summary>Flash files of app-paid orders that could not be sent yet, again every few minutes.</summary>
    /// <summary>
    /// Reports the kiosk's own state every <c>Kiosk.TelemetrySeconds</c> (plan
    /// section 7.9). A report that does not get through is simply the next
    /// one's job: nothing is queued, the state it described is already old.
    /// </summary>
    private async Task SendTelemetryAsync(
        KioskRuntime runtime,
        KioskHubClient hub,
        CommandProcessor commands,
        IRemovableDriveWatcher drives,
        KioskApiClient api,
        CancellationToken cancellationToken)
    {
        var collector = _services.GetRequiredService<KioskTelemetryCollector>();
        var paths = _services.GetRequiredService<IAppPaths>();
        var uploads = _services.GetRequiredService<FlashUploads>();
        var crashes = _services.GetRequiredService<CrashLoopGuard>();
        var converters = _services.GetRequiredService<ConverterPaths>();

        TelemetryScreenState screen = new();

        var sources = new TelemetrySources
        {
            Screen = () => screen,
            Hub = () => hub.State,
            FlashInserted = async token => (await drives.ListAsync(token).ConfigureAwait(false)).Count > 0,
            SpoolFiles = () => CountFiles(paths.SpoolDirectory),
            FailedFlashUploads = () => uploads.Pending,
            PendingCommands = () => commands.InProgress,
            LastCrashAt = () => crashes.LastCrashAt,
            LibreOffice = () => ProgramExists(converters.LibreOfficeExecutable),
            Ghostscript = () => ProgramExists(converters.GhostscriptFor("ps")),
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            await TryAsync("telemetry", async () =>
                {
                    // The screen is read where it lives; the rest needs no thread of its own.
                    screen = await Dispatcher.UIThread.InvokeAsync(ReadScreen).GetTask().ConfigureAwait(false);

                    var report = await collector.CollectAsync(sources, cancellationToken).ConfigureAwait(false);

                    await api.SendTelemetryAsync(report, cancellationToken).ConfigureAwait(false);
                })
                .ConfigureAwait(false);

            await Task.Delay(runtime.TelemetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports what the printers say about themselves (plan section 7.10): the
    /// selected printer every minute, the other printers every ten.
    /// </summary>
    private async Task SendPrinterTelemetryAsync(
        KioskRuntime runtime, PrinterReporter reporter, KioskApiClient api, CancellationToken cancellationToken)
    {
        var inspector = _services.GetRequiredService<IPrinterInspector>();
        var schedule = new PrinterTelemetrySchedule();

        while (!cancellationToken.IsCancellationRequested)
        {
            await TryAsync("printer telemetry", async () =>
                {
                    var due = schedule.Due(
                        runtime.Configuration?.SelectedPrinter, reporter.Current.Select(printer => printer.Name), DateTime.UtcNow);

                    var readings = new List<PrinterDetails>();

                    foreach (var printer in due)
                    {
                        readings.Add(await inspector.InspectAsync(printer, cancellationToken).ConfigureAwait(false));
                    }

                    if (readings.Count > 0)
                    {
                        await api.SendPrinterTelemetryAsync(readings, cancellationToken).ConfigureAwait(false);
                    }
                })
                .ConfigureAwait(false);

            await Task.Delay(PrinterTelemetrySchedule.Interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private TelemetryScreenState ReadScreen()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        // The screen the window is on; a display server that names no primary
        // screen (a kiosk compositor, Xvfb) still has the one it draws on.
        var bounds = window is null
            ? null
            : (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault())?.Bounds;

        // The window reads the scanner and the model keeps when; the collector
        // takes it from the counters.
        if (_model.LastScanAt is { } scanned)
        {
            _services.GetRequiredService<TelemetryCounters>().QrRead(scanned);
        }

        return _model.TelemetryState(bounds?.Width, bounds?.Height);
    }

    private static int? CountFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Count() : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a converter is installed: a full path as it is, a bare name on PATH.</summary>
    private static bool ProgramExists(string program)
    {
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(program);
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, program)));
    }

    private async Task SendUploadsAsync(KioskOrderService orders, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(UploadRetryInterval, cancellationToken).ConfigureAwait(false);
            await TryAsync("flash uploads", () => orders.SendPendingUploadsAsync(cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After the printer stopped an order: once the backend has made the refund
    /// (scenario A), the screen shows what goes back and the QR to follow it.
    /// </summary>
    private async Task ShowRefundNoticeAsync(KioskOrderService orders, Guid jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            IReadOnlyList<RefundInfo> refunds;

            try
            {
                refunds = await orders.GetRefundsAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or KioskApiException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (refunds.Count > 0)
            {
                var amount = refunds.Sum(r => r.Amount);
                await OnUiAsync(() =>
                {
                    // The customer still on this order's receipt screen reads it there (T-133);
                    // one who has gone on finds it on the home screen.
                    if (!(_model.Screen == KioskScreen.Flow && _model.Flow?.ShowPrinterRefund(jobId, amount, refunds[0].Destination, refunds[0].StatusUrl) == true))
                    {
                        _model.ShowRefund(amount, refunds[0].Destination, refunds[0].StatusUrl);
                    }
                }).ConfigureAwait(false);
                return;
            }
        }
    }

    private void SetUpFlow(KioskOrderService orders, IRemovableDriveWatcher drives, List<Action> unsubscribe)
    {
        var flow = new PrintFlowViewModel(orders, drives, _services.GetRequiredService<IKioskAnalytics>()) { Language = _model.Language };

        flow.Finished += () =>
        {
            _model.Screen = _model.RestingScreen;
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

        Action<string> userQr = async code =>
        {
            Touch();
            _model.Screen = KioskScreen.Flow;
            await flow.StartUserQrAsync(code).ConfigureAwait(true);
        };

        _model.PrintFromUsbRequested += printFromUsb;
        _model.CancelByCodeRequested += cancelByCode;
        _model.UserQrRequested += userQr;

        unsubscribe.Add(() =>
        {
            _model.PrintFromUsbRequested -= printFromUsb;
            _model.CancelByCodeRequested -= cancelByCode;
            _model.UserQrRequested -= userQr;
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

        if (_model.ResetIfIdle(DateTime.UtcNow - _lastTouch, limit, runtime.Configuration?.DefaultLanguage))
        {
            _logger.LogInformation("Nobody touched the screen; the order and the language were reset.");
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

    /// <summary>Sends the waiting analytics once a minute; offline they wait on disk.</summary>
    private async Task SendAnalyticsAsync(KioskApiClient api, CancellationToken cancellationToken)
    {
        var queue = _services.GetRequiredService<AnalyticsQueue>();

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(AnalyticsInterval, cancellationToken).ConfigureAwait(false);
            await TryAsync("analytics", () => queue.FlushAsync(api.SendAnalyticsAsync, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task CatchUpAsync(KioskRuntime runtime, CommandProcessor commands, KioskApiClient api, CancellationToken cancellationToken)
    {
        await TryAsync("configuration", () => runtime.RefreshAsync(cancellationToken)).ConfigureAwait(false);

        if (!runtime.SafeMode)
        {
            await TryAsync("open commands", () => commands.CatchUpAsync(cancellationToken)).ConfigureAwait(false);
        }

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
