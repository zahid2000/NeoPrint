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

/// <summary>A restart or a reboot this kiosk has acknowledged and is about to carry out.</summary>
/// <param name="NotedAt">This machine's clock when the note was written.</param>
/// <param name="UptimeSeconds">How long the machine had been up then.</param>
public sealed record PendingCommand(Guid CommandId, string Type, DateTime NotedAt, long UptimeSeconds);

/// <summary>
/// The one command the kiosk is in the middle of when it goes down on purpose
/// (plan section 7.8), kept on disk so the application that comes back knows
/// it has carried it out - whatever its clock says, and whether or not it can
/// reach the backend yet.
/// </summary>
/// <remarks>
/// Not a secret and not about a customer: a command id, a time and an uptime.
/// A file that cannot be read is no note; the backend's own mark
/// (<c>Acknowledged</c>) still keeps the command from being carried out twice.
/// </remarks>
public sealed class PendingCommandStore
{
    private readonly string? _file;
    private PendingCommand? _held;

    /// <param name="stateDirectory">Where the note is kept; null keeps it in memory only (tests, a kiosk without a state folder).</param>
    public PendingCommandStore(string? stateDirectory)
        => _file = stateDirectory is null ? null : Path.Combine(stateDirectory, "pending-command.json");

    public void Note(PendingCommand command)
    {
        _held = command;

        if (_file is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);

        // Written beside and moved into place: a power cut leaves the old note or the new one, not half of one.
        var draft = _file + ".tmp";
        File.WriteAllText(draft, System.Text.Json.JsonSerializer.Serialize(command));
        File.Move(draft, _file, overwrite: true);
    }

    /// <summary>The note for this command, if there is one.</summary>
    public PendingCommand? Find(Guid commandId)
    {
        var noted = _held ?? Read();

        return noted?.CommandId == commandId ? noted : null;
    }

    public void Clear()
    {
        _held = null;

        if (_file is not null && File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    private PendingCommand? Read()
    {
        if (_file is null || !File.Exists(_file))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PendingCommand>(File.ReadAllText(_file));
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

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
    private readonly Func<bool> _safeMode;
    private readonly PendingCommandStore _pending;
    private readonly Func<Guid, string, bool, CancellationToken, Task<(bool, string, int)?>>? _agentCommands;
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
        IClock? clock = null,
        Func<bool>? safeMode = null,
        PendingCommandStore? pending = null,
        Func<Guid, string, bool, CancellationToken, Task<(bool, string, int)?>>? agentCommands = null)
    {
        _agentCommands = agentCommands;
        _pending = pending ?? new PendingCommandStore(null);
        _safeMode = safeMode ?? (() => false);
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
            await HandleAsync(
                    command.Id,
                    command.Type,
                    cancellationToken,
                    command.CreatedAt,
                    acknowledged: string.Equals(command.Status, "Acknowledged", StringComparison.Ordinal))
                .ConfigureAwait(false);
        }
    }

    /// <param name="sentAt">
    /// When the command was sent, for one read back from the backend. A
    /// command that arrives live over the hub has none: it is newer than this
    /// process by definition.
    /// </param>
    /// <param name="acknowledged">
    /// The backend already has this kiosk's "I will do it" for the command: it
    /// was marked before it was carried out, and is only answered now.
    /// </param>
    public async Task HandleAsync(
        Guid commandId, string type, CancellationToken cancellationToken, DateTime? sentAt = null, bool acknowledged = false)
    {
        // In safe mode the kiosk prints nothing (plan section 7.12), so a
        // printer command waits - it is still open when safe mode is cleared.
        // Pausing, restarting and rebooting are what a kiosk in safe mode
        // most needs to be reachable for.
        if (_safeMode() && type is "RefreshPrinters" or "TestPrint")
        {
            return;
        }

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
                "RestartApp" => await RestartAsync(commandId, sentAt, acknowledged, cancellationToken).ConfigureAwait(false),
                "Reboot" => await RebootAsync(commandId, sentAt, acknowledged, cancellationToken).ConfigureAwait(false),

                // The agent's commands (T-321): the update coordinator acknowledges
                // them and answers when they are done, maybe from the next process.
                "UpdateNow" or "RollbackVersion" or "UploadLogs" or "ReapplyProvisioning" or "RestartAgent" when _agentCommands is not null
                    => await _agentCommands(commandId, type, acknowledged, cancellationToken).ConfigureAwait(false),
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
    /// </summary>
    /// <remarks>
    /// The command is marked before it is carried out - acknowledged on the
    /// backend, noted on disk - and a command that carries either mark is
    /// never carried out again, only answered. That is what keeps a restart
    /// from repeating itself: the application that comes back finds the same
    /// command still open, and without the mark it would have nothing but two
    /// clocks, its own and the server's, to tell whether it had done it.
    /// </remarks>
    private async Task<(bool, string, int)?> RestartAsync(
        Guid commandId, DateTime? sentAt, bool acknowledged, CancellationToken cancellationToken)
    {
        // Marked: this is the application that came back.
        if (acknowledged || _pending.Find(commandId) is not null)
        {
            _pending.Clear();
            return (true, "kioskCommands.restarted", 0);
        }

        // Sent while the kiosk was switched off: starting was the restart.
        if (sentAt is { } sent && sent < _startedAt)
        {
            return (true, "kioskCommands.restarted", 0);
        }

        // Not acknowledged means not done: if this fails, nothing restarts
        // and the command is taken again later.
        await _api.AcknowledgeCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        await NoteAsync(commandId, "RestartApp", cancellationToken).ConfigureAwait(false);

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
    /// Restarts the machine once the printer is free. Marked before it is
    /// carried out, like <see cref="RestartAsync"/> - a machine that rebooted
    /// for a command must never reboot for it again - and answered afterwards
    /// by the machine's own uptime.
    /// </summary>
    private async Task<(bool, string, int)?> RebootAsync(
        Guid commandId, DateTime? sentAt, bool acknowledged, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var uptime = await _uptimeSeconds(cancellationToken).ConfigureAwait(false);

        if (_pending.Find(commandId) is { } noted)
        {
            _pending.Clear();

            // The uptime counter started again, or the machine has been up
            // for less time than has passed since the note was written: both
            // are read off this machine alone, no server clock involved.
            return uptime is not { } seconds
                ? (true, "kioskCommands.restarted", 0)
                : seconds < noted.UptimeSeconds || seconds < (now - noted.NotedAt).TotalSeconds
                    ? (true, "kioskCommands.rebooted", 0)
                    : (false, "kioskCommands.rebootNotDone", 0);
        }

        var bootedSinceItWasSent = sentAt is { } sent && uptime is { } up && sent < now.AddSeconds(-up);

        if (acknowledged)
        {
            // Acknowledged, and the note is gone: answered as well as the
            // clocks allow, and in no case carried out a second time.
            return bootedSinceItWasSent ? (true, "kioskCommands.rebooted", 0) : (false, "kioskCommands.rebootNotDone", 0);
        }

        // Sent while the machine was off: booting was the reboot.
        if (bootedSinceItWasSent)
        {
            return (true, "kioskCommands.rebooted", 0);
        }

        await _api.AcknowledgeCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        await NoteAsync(commandId, "Reboot", cancellationToken).ConfigureAwait(false);

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
                        _pending.Clear();
                        return (false, "kioskCommands.rebootRefused", 0);
                    }

                    // The reboot takes this process with it. Still here after
                    // a while means it was refused or never happened.
                    await Task.Delay(RebootGrace, cancellationToken).ConfigureAwait(false);

                    _logger.LogError("Command {CommandId}: the machine did not restart.", commandId);
                    _pending.Clear();
                    return (false, "kioskCommands.rebootRefused", 0);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task NoteAsync(Guid commandId, string type, CancellationToken cancellationToken)
        => _pending.Note(new PendingCommand(
            commandId,
            type,
            _clock.UtcNow,
            await _uptimeSeconds(cancellationToken).ConfigureAwait(false) ?? 0));

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
