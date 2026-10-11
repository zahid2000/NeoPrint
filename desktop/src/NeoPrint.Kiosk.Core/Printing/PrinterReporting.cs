using System.Text.Json;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// Tells the backend which printers this kiosk can see: at start, every
/// minute when the list changed, and when asked (plan section 7.7).
/// </summary>
public sealed class PrinterReporter
{
    private readonly IPrinterService _printers;
    private readonly Func<IReadOnlyCollection<DetectedPrinter>, CancellationToken, Task> _report;
    private readonly ILogger<PrinterReporter> _logger;
    private string? _lastReported;

    public PrinterReporter(
        IPrinterService printers,
        Func<IReadOnlyCollection<DetectedPrinter>, CancellationToken, Task> report,
        ILogger<PrinterReporter> logger)
    {
        _printers = printers;
        _report = report;
        _logger = logger;
    }

    /// <summary>The list as last discovered.</summary>
    public IReadOnlyList<DetectedPrinter> Current { get; private set; } = [];

    /// <summary>Discovers and reports; unless <paramref name="force"/>, only when the list changed.</summary>
    /// <returns>Whether a report was sent.</returns>
    public async Task<bool> ReportAsync(bool force, CancellationToken cancellationToken)
    {
        var found = await _printers.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        Current = found;

        var fingerprint = JsonSerializer.Serialize(found.OrderBy(printer => printer.Name, StringComparer.Ordinal));

        if (!force && fingerprint == _lastReported)
        {
            return false;
        }

        await _report(found, cancellationToken).ConfigureAwait(false);
        _lastReported = fingerprint;

        _logger.LogInformation("Reported {Count} printers to the backend.", found.Count);

        return true;
    }
}

/// <summary>The kiosk's built-in test page (plan section 7.7).</summary>
public interface ITestPageSource
{
    /// <summary>Writes a one-page PDF naming the kiosk, the printer and the time, with colour and duplex checks.</summary>
    Task<string> CreateAsync(string folder, TestPageInfo info, CancellationToken cancellationToken);
}

/// <param name="Lines">What the page says, already in the kiosk's language: title first.</param>
public sealed record TestPageInfo(IReadOnlyList<string> Lines);

/// <summary>
/// Carries out the commands the panel sends (plan section 7.8): the printer
/// ones in step 3.
/// </summary>
/// <remarks>
/// A command arrives over the hub, or from <c>GET kiosks/me/commands</c>
/// after a reconnect, so the same one can come twice; the ids already done
/// are remembered and the backend ignores a repeated answer anyway.
/// </remarks>
public sealed class CommandProcessor
{
    private readonly KioskApiClient _api;
    private readonly PrinterReporter _reporter;
    private readonly PrintQueueWorker _worker;
    private readonly IPrinterService _printers;
    private readonly ITestPageSource _testPage;
    private readonly SpoolStore _spool;
    private readonly Func<KioskConfiguration?> _configuration;
    private readonly Func<KioskConfiguration, string, DateTime, IReadOnlyList<string>> _testPageText;
    private readonly ILogger<CommandProcessor> _logger;
    private readonly Func<CancellationToken, Task> _reloadConfiguration;
    private readonly ISystemControl _system;
    private readonly Action _leavingOnPurpose;
    private readonly Func<CancellationToken, Task<long?>> _uptimeSeconds;
    private readonly IClock _clock;
    private readonly DateTime _startedAt;
    private readonly HashSet<Guid> _done = [];

    /// <summary>How long a reboot may take to take the application down before it is called a failure.</summary>
    public TimeSpan RebootGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Commands received and still being carried out; telemetry reports the count (plan section 7.9).</summary>
    public int InProgress => _done.Count;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandProcessor(
        KioskApiClient api,
        PrinterReporter reporter,
        PrintQueueWorker worker,
        IPrinterService printers,
        ITestPageSource testPage,
        SpoolStore spool,
        Func<KioskConfiguration?> configuration,
        Func<KioskConfiguration, string, DateTime, IReadOnlyList<string>> testPageText,
        ILogger<CommandProcessor> logger,
        Func<CancellationToken, Task>? reloadConfiguration = null,
        ISystemControl? system = null,
        Action? leavingOnPurpose = null,
        Func<CancellationToken, Task<long?>>? uptimeSeconds = null,
        IClock? clock = null)
    {
        _reloadConfiguration = reloadConfiguration ?? (_ => Task.CompletedTask);
        _system = system ?? new FakeSystemControl();
        _leavingOnPurpose = leavingOnPurpose ?? (() => { });
        _uptimeSeconds = uptimeSeconds ?? (_ => Task.FromResult<long?>(null));
        _clock = clock ?? new SystemClock();
        _startedAt = _clock.UtcNow;
        _testPageText = testPageText;
        _api = api;
        _reporter = reporter;
        _worker = worker;
        _printers = printers;
        _testPage = testPage;
        _spool = spool;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Picks up what arrived while the kiosk was not listening.</summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        foreach (var command in await _api.GetOpenCommandsAsync(cancellationToken).ConfigureAwait(false))
        {
            await HandleAsync(command.Id, command.Type, cancellationToken, command.CreatedAt).ConfigureAwait(false);
        }
    }

    /// <param name="sentAt">
    /// When the command was sent, for one read back from the backend. A
    /// command that arrives live over the hub has none: it is newer than this
    /// process by definition.
    /// </param>
    public async Task HandleAsync(Guid commandId, string type, CancellationToken cancellationToken, DateTime? sentAt = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_done.Add(commandId))
            {
                return;
            }

            _logger.LogInformation("Command {CommandId}: {Type}.", commandId, type);

            var answer = type switch
            {
                "RefreshPrinters" => await RefreshAsync(cancellationToken).ConfigureAwait(false),
                "TestPrint" => await TestPrintAsync(cancellationToken).ConfigureAwait(false),
                "Pause" => await ReloadAsync("kioskCommands.paused", cancellationToken).ConfigureAwait(false),
                "Resume" => await ReloadAsync("kioskCommands.resumed", cancellationToken).ConfigureAwait(false),
                "RestartApp" => await RestartAsync(commandId, sentAt, cancellationToken).ConfigureAwait(false),
                "Reboot" => await RebootAsync(commandId, sentAt, cancellationToken).ConfigureAwait(false),
                _ => (false, "kioskCommands.unsupported", 0),
            };

            // No answer yet: the application is on its way down, and the one
            // that comes up afterwards answers (see RestartAsync).
            if (answer is not var (succeeded, messageKey, sheets))
            {
                return;
            }

            await _api.CompleteCommandAsync(commandId, succeeded, messageKey, sheets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
        {
            // Not answered: it stays open on the backend and is picked up again.
            _done.Remove(commandId);
            _logger.LogError(exception, "Command {CommandId} ({Type}) failed; it will be tried again.", commandId, type);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool, string, int)?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _reporter.ReportAsync(force: true, cancellationToken).ConfigureAwait(false);
        return (true, "kioskCommands.printersReported", 0);
    }

    /// <summary>
    /// Pause and resume are decided on the backend and are part of the
    /// configuration (its status): the kiosk reads it again and its screen
    /// follows. A kiosk that missed the command finds the same thing at its
    /// next heartbeat.
    /// </summary>
    private async Task<(bool, string, int)?> ReloadAsync(string done, CancellationToken cancellationToken)
    {
        await _reloadConfiguration(cancellationToken).ConfigureAwait(false);
        return (true, done, 0);
    }

    /// <summary>
    /// Restarts the application once the printer is free (plan section 7.8).
    /// A command sent before this process started has been carried out by the
    /// start itself - that is how the restarted application answers it, and
    /// why a kiosk that was off when it was sent does not restart once more.
    /// </summary>
    private async Task<(bool, string, int)?> RestartAsync(Guid commandId, DateTime? sentAt, CancellationToken cancellationToken)
    {
        if (sentAt is { } sent && sent < _startedAt)
        {
            return (true, "kioskCommands.restarted", 0);
        }

        await _api.AcknowledgeCommandAsync(commandId, cancellationToken).ConfigureAwait(false);

        await _worker.WithPrinterAsync(
                async () =>
                {
                    _logger.LogWarning("Command {CommandId}: restarting the application.", commandId);

                    // Not a crash: the crash-loop guard must not count it.
                    _leavingOnPurpose();
                    await _system.RestartApplicationAsync(cancellationToken).ConfigureAwait(false);

                    return (true, string.Empty, 0);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Restarts the machine once the printer is free. Answered after the
    /// reboot, by the machine's own uptime: shorter than the command is old
    /// means it did restart.
    /// </summary>
    private async Task<(bool, string, int)?> RebootAsync(Guid commandId, DateTime? sentAt, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (sentAt is { } sent)
        {
            if (await _uptimeSeconds(cancellationToken).ConfigureAwait(false) is { } uptime && sent < now.AddSeconds(-uptime))
            {
                return (true, "kioskCommands.rebooted", 0);
            }

            if (sent < _startedAt)
            {
                // The application came back and the machine never went down.
                return (false, "kioskCommands.rebootNotDone", 0);
            }
        }

        await _api.AcknowledgeCommandAsync(commandId, cancellationToken).ConfigureAwait(false);

        return await _worker.WithPrinterAsync<(bool, string, int)?>(
                async () =>
                {
                    _logger.LogWarning("Command {CommandId}: restarting the machine.", commandId);

                    try
                    {
                        await _system.RebootAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        _logger.LogError(exception, "Command {CommandId}: the machine could not be restarted.", commandId);
                        return (false, "kioskCommands.rebootNotDone", 0);
                    }

                    // The reboot takes this process with it. Still here after
                    // a while means it was refused or never happened.
                    await Task.Delay(RebootGrace, cancellationToken).ConfigureAwait(false);

                    _logger.LogError("Command {CommandId}: the machine did not restart.", commandId);
                    return (false, "kioskCommands.rebootNotDone", 0);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the job at the printer, then prints the test page before the
    /// next one; it takes no place in the queue and costs nothing (plan 7.7).
    /// </summary>
    private Task<(bool, string, int)?> TestPrintAsync(CancellationToken cancellationToken)
        => _worker.WithPrinterAsync<(bool, string, int)?>(
            async () =>
            {
                var configuration = _configuration();
                var printer = configuration?.SelectedPrinter;

                if (configuration is null || string.IsNullOrWhiteSpace(printer))
                {
                    return (false, "printer.notSelected", 0);
                }

                var folder = _spool.NewWorkFolder();

                try
                {
                    var pdf = await _testPage.CreateAsync(
                            folder,
                            new TestPageInfo(_testPageText(configuration, printer, DateTime.UtcNow)),
                            cancellationToken)
                        .ConfigureAwait(false);

                    var outcome = await _printers.PrintAsync(
                            new PrintRequest(printer, pdf, 1, false, configuration.EffectiveCapabilities.Color, "A4", "NeoPrint-test"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return outcome.Succeeded
                        ? (true, "kioskCommands.testPagePrinted", 1)
                        : (false, outcome.ErrorCode ?? "printer.error", 0);
                }
                finally
                {
                    _spool.Discard(folder);
                }
            },
            cancellationToken);
}
