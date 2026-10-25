using System.Text;

using Microsoft.Extensions.Logging;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Logging;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>What the screen side of the kiosk tells and is told about an update.</summary>
public interface IUpdateHost
{
    /// <summary>
    /// No customer and nothing at the printer: no order on screen, no payment
    /// under way, no phone connected, no job being printed (a paused or
    /// out-of-service kiosk with paid work waiting counts as idle - the work is
    /// in the spool and goes on afterwards).
    /// </summary>
    bool IsIdle { get; }

    /// <summary>Turn new customers away ("Kiosk bir neçə dəqiqəyə yenilənir") or take them again.</summary>
    void SetDraining(bool draining);

    /// <summary>The "Kiosk yenilənir" screen: the UI is about to be stopped.</summary>
    void SetUpdating(bool updating);
}

/// <summary>
/// The UI's side of an update (docs/KIOSK_UPDATES.md, T-321): asks the backend,
/// downloads, checks, waits for the update window and a moment with no
/// customer, hands the package to the agent and reports every step; and
/// carries out the agent's remote commands.
/// </summary>
public sealed class UpdateCoordinator
{
    private static readonly TimeSpan MomentPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultPoll = TimeSpan.FromSeconds(2);

    /// <summary>How often a package waiting for its moment is asked about again: withdrawn, or made urgent.</summary>
    private static readonly TimeSpan OfferRecheck = TimeSpan.FromMinutes(5);

    private readonly KioskApiClient _api;
    private readonly UpdatePaths _paths;
    private readonly ReleasePackageCheck _check;
    private readonly IUpdateHost _host;
    private readonly Func<KioskConfiguration?> _configuration;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly string _appVersion;
    private readonly string _logDirectory;
    private readonly Func<DateTime, TimeOnly> _localTime;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ReleaseDownloader _downloader;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly HashSet<Guid> _refused = [];
    private readonly Action _leavingOnPurpose;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // "İndi yenilə" while a package already waits for its moment: that wait
    // stops waiting for the window, and the command goes with its request.
    private volatile bool _hurry;
    private Guid? _hurryCommand;
    private volatile bool _recheck;

    public UpdateCoordinator(
        KioskApiClient api,
        UpdatePaths paths,
        ReleasePackageCheck check,
        IUpdateHost host,
        Func<KioskConfiguration?> configuration,
        IClock clock,
        ILogger logger,
        string appVersion,
        string logDirectory,
        int downloadKilobytesPerSecond = 0,
        Func<DateTime, TimeOnly>? localTime = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action? leavingOnPurpose = null)
    {
        _leavingOnPurpose = leavingOnPurpose ?? (() => { });
        _api = api;
        _paths = paths;
        _check = check;
        _host = host;
        _configuration = configuration;
        _clock = clock;
        _logger = logger;
        _appVersion = appVersion;
        _logDirectory = logDirectory;
        _localTime = localTime ?? (utc => TimeOnly.FromDateTime(utc.ToLocalTime()));
        _delay = delay ?? Task.Delay;
        _downloader = new ReleaseDownloader(api, paths, downloadKilobytesPerSecond, logger, _delay);
    }

    /// <summary>What the agent wrote about the machine; null while there is no agent (a developer machine).</summary>
    public AgentState? Agent => AgentFiles.Read<AgentState>(_paths.StateFile);

    private KioskUpdateSettings Settings => _configuration()?.Update ?? new KioskUpdateSettings();

    /// <summary>Ask the backend now (the hub said a rollout reaches this kiosk).</summary>
    public void Wake()
    {
        _recheck = true;
        _wake.TrySetResult();
    }

    /// <summary>
    /// The UI is up and working: connected, a heartbeat answered, the printers
    /// looked at, not in safe mode. The agent waits for exactly this after a switch.
    /// </summary>
    public void MarkHealthy()
    {
        try
        {
            var marker = _paths.HealthMarker(UpdateComponents.App, _appVersion);
            if (!File.Exists(marker))
            {
                Directory.CreateDirectory(_paths.Health);
                File.WriteAllText(marker, _clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                _logger.LogInformation("Kiosk UI {Version} is healthy.", _appVersion);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The health mark could not be written.");
        }
    }

    /// <summary>Asks, at start and then every <c>Update.CheckIntervalMinutes</c> or when woken.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // What the agent answered while this UI was down - a rollback above all -
        // reaches the backend before anything is asked: else the version just
        // taken back would still be offered.
        try
        {
            await ReportResultsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
        {
            _logger.LogWarning(exception, "The agent's answers could not be passed on yet.");
        }

        // The agent answers a switch of the UI only after this process says it
        // is healthy: its answers are looked for on their own clock.
        _ = Task.Run(() => ReportResultsLoopAsync(cancellationToken), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(force: false, commandId: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
            {
                _logger.LogWarning(exception, "The update check failed; it runs again later.");
            }

            var wake = _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interval = TimeSpan.FromMinutes(Math.Clamp(Settings.CheckIntervalMinutes, 1, 24 * 60));
            await Task.WhenAny(wake.Task, _delay(interval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>One look at what is offered; applies the first component that is newer than what runs.</summary>
    /// <param name="force">"İndi yenilə": no update window, and a version refused here before is tried again.</param>
    /// <returns>Whether anything was started.</returns>
    public async Task<bool> CheckAsync(bool force, Guid? commandId, CancellationToken cancellationToken)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The agent is in the middle of a switch (this UI may be the version
            // being checked): nothing new is asked for until it is done.
            if (File.Exists(_paths.JournalFile))
            {
                return false;
            }

            var offers = await _api.GetUpdateOffersAsync(cancellationToken).ConfigureAwait(false);
            _downloader.Forget(offers.Offers.Select(offer => offer.ReleaseId));
            var agent = Agent ?? new AgentState();

            foreach (var component in UpdateComponents.InOrder)
            {
                if (offers.Offers.FirstOrDefault(offer => offer.Component == component) is not { } offer
                    || !ReleaseVersion.TryParse(offer.Version, out var offered))
                {
                    continue;
                }

                var installed = Installed(agent, component);
                if (installed is not null && ReleaseVersion.TryParse(installed, out var running) && offered.CompareTo(running) <= 0)
                {
                    continue;
                }

                // Refused here, or failed its health check here: not again unless an administrator asks.
                if (!force && (_refused.Contains(offer.ReleaseId) || agent.FailedHere.Contains($"{component}@{offer.Version}")))
                {
                    continue;
                }

                await ApplyAsync(offer, offers.Withdrawn, agent, force, commandId, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// An agent command (plan section 7.12; KIOSK_UPDATES.md section 5):
    /// acknowledged now and answered when it is done - by this process, or for
    /// a switch of the UI by the one that starts afterwards.
    /// </summary>
    /// <returns>The answer when there is one already; null when it comes later.</returns>
    public async Task<(bool, string, int)?> HandleCommandAsync(Guid commandId, string type, bool acknowledged, CancellationToken cancellationToken)
    {
        if (AgentFiles.Read<AgentResult>(ResultPath(commandId)) is { } done)
        {
            MarkReported(done.Id);
            return Answer(done);
        }

        if (!acknowledged)
        {
            await _api.AcknowledgeCommandAsync(commandId, cancellationToken).ConfigureAwait(false);
        }

        // Asked by the UI the switch replaced, and the agent is still at it - its
        // health check, or the request not taken yet: the answer comes with its
        // result. Carried out again, "İndi yenilə" found nothing new and said so
        // over the update it had just made (T-321).
        if (File.Exists(Path.Combine(_paths.Requests, $"{commandId:N}.json"))
            || AgentFiles.Read<AgentJournal>(_paths.JournalFile)?.CommandId == commandId)
        {
            _logger.LogInformation("Command {CommandId} ({Type}) is with the agent already; its answer comes with the agent's.", commandId, type);
            return null;
        }

        _ = Task.Run(() => CarryOutAsync(commandId, type, cancellationToken), cancellationToken);
        return null;
    }

    private async Task CarryOutAsync(Guid commandId, string type, CancellationToken cancellationToken)
    {
        try
        {
            switch (type)
            {
                case "UpdateNow":
                    if (_running.CurrentCount == 0)
                    {
                        // A package is waiting for its moment already: it goes now, with this command.
                        _hurryCommand = commandId;
                        _hurry = true;
                        _recheck = true;
                        break;
                    }

                    if (!await CheckAsync(force: true, commandId, cancellationToken).ConfigureAwait(false))
                    {
                        await _api.CompleteCommandAsync(commandId, true, "updates.result.nothing", 0, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case "UploadLogs":
                    var gzip = LogCollector.Collect(_logDirectory, Math.Clamp(Settings.MaxLogUploadMb, 1, 100) * 1024L * 1024L, _clock.UtcNow);
                    await _api.UploadLogsAsync(gzip, commandId, cancellationToken).ConfigureAwait(false);
                    await _api.CompleteCommandAsync(commandId, true, "kioskCommands.logsUploaded", 0, cancellationToken).ConfigureAwait(false);
                    break;

                case "RollbackVersion":
                    await AskAgentAsync(new AgentRequest(commandId, AgentRequestKind.Rollback, _clock.UtcNow, commandId,
                        HealthTimeoutSeconds: HealthSeconds, KeepVersions: Settings.KeepVersions), moment: true, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "ReapplyProvisioning":
                    await AskAgentAsync(new AgentRequest(commandId, AgentRequestKind.ReapplyProvisioning, _clock.UtcNow, commandId),
                        moment: true, null, cancellationToken).ConfigureAwait(false);
                    break;

                case "RestartAgent":
                    await AskAgentAsync(new AgentRequest(commandId, AgentRequestKind.RestartAgent, _clock.UtcNow, commandId),
                        moment: false, null, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    await _api.CompleteCommandAsync(commandId, false, "kioskCommands.unsupported", 0, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The kiosk is going down; the command stays open and is taken again.
        }
        catch (Exception exception) when (exception is not DeviceRevokedException)
        {
            _logger.LogError(exception, "Command {CommandId} ({Type}) failed.", commandId, type);
            try
            {
                await _api.CompleteCommandAsync(commandId, false, type == "UploadLogs" ? "kioskCommands.logsFailed" : "updates.reasonUnknown", 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception answer) when (answer is not OperationCanceledException)
            {
                _logger.LogWarning(answer, "Command {CommandId} could not be answered; it stays open.", commandId);
            }
        }
    }

    // ---------------------------------------------------------------------

    private int HealthSeconds => Math.Clamp(Settings.HealthTimeoutMinutes, 1, 30) * 60;

    // The UI's own version is what runs, whatever the agent has written down yet.
    private string? Installed(AgentState agent, string component)
        => component == UpdateComponents.App ? _appVersion : agent.Installed.GetValueOrDefault(component);

    private async Task ApplyAsync(
        KioskReleaseOffer offer, IReadOnlyList<string> withdrawn, AgentState agent, bool force, Guid? commandId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("{Component} {Version} is offered (urgent: {Urgent}).", offer.Component, offer.Version, offer.Urgent);
        await ReportAsync(offer, "Downloading", null, cancellationToken).ConfigureAwait(false);

        string package;
        try
        {
            package = await _downloader.FetchAsync(offer, cancellationToken).ConfigureAwait(false);
        }
        catch (ReleaseDownloadException exception)
        {
            // A line that dropped is not an incident: tried again at the next check, from where it stopped.
            _logger.LogWarning(exception, "{Component} {Version} could not be downloaded yet.", offer.Component, offer.Version);
            await ReportAsync(offer, "Downloading", "updates.downloadFailed", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The UI's own check: no time is lost waiting for a moment with a package the agent would refuse.
        var check = await _check.CheckAsync(
                Encoding.UTF8.GetBytes(offer.Manifest),
                Encoding.UTF8.GetBytes(offer.Signature),
                package,
                new ReleaseExpectation(
                    Enum.TryParse<ReleaseComponent>(offer.Component, out var component) ? component : ReleaseComponent.App,
                    UpdatePaths.Platform,
                    _check.KeySet,
                    Installed(agent, offer.Component) is { } installed && ReleaseVersion.TryParse(installed, out var running) ? running : null,
                    agent.AgentVersion is { } agentVersion && ReleaseVersion.TryParse(agentVersion, out var runningAgent) ? runningAgent : ReleaseVersion.Parse("0.0.0"),
                    withdrawn),
                cancellationToken)
            .ConfigureAwait(false);

        if (!check.Accepted)
        {
            _logger.LogWarning("{Component} {Version} refused here: {Rejection} - {Detail}", offer.Component, offer.Version, check.Rejection, check.Detail);
            File.Delete(package);
            _refused.Add(offer.ReleaseId);
            await ReportAsync(offer, "Failed", ReleasePackageCheck.MessageKey(check.Rejection), cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReportAsync(offer, "Ready", null, cancellationToken).ConfigureAwait(false);

        var request = new AgentRequest(
            commandId ?? Guid.NewGuid(), AgentRequestKind.Install, _clock.UtcNow, commandId, offer.ReleaseId,
            Path.GetFileName(package), offer.Manifest, offer.Signature, withdrawn, HealthSeconds, Settings.KeepVersions, Retry: force);

        await AskAgentAsync(request, moment: true, offer, cancellationToken, windowFree: force || offer.Urgent).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the moment (when asked to), hands the request to the agent and
    /// reports what it answers. For a switch of the UI there is no answer here:
    /// the agent stops this process, and the next one reports.
    /// </summary>
    private async Task AskAgentAsync(AgentRequest request, bool moment, KioskReleaseOffer? offer, CancellationToken cancellationToken, bool windowFree = true)
    {
        if (moment && !await WaitForMomentAsync(offer, windowFree, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_hurry && offer is not null)
        {
            request = request with { CommandId = request.CommandId ?? _hurryCommand, Retry = true };
            _hurry = false;
            _hurryCommand = null;
        }

        var stopsUi = request.Kind is AgentRequestKind.Rollback || offer?.Component == UpdateComponents.App;
        _host.SetUpdating(stopsUi);
        if (stopsUi)
        {
            // The agent is about to end this process on purpose: not a crash,
            // or a few updates would put the kiosk into safe mode (T-321).
            _leavingOnPurpose();
        }

        Directory.CreateDirectory(_paths.Requests);
        AgentFiles.Write(Path.Combine(_paths.Requests, $"{request.Id:N}.json"), request);
        if (offer is not null)
        {
            await ReportAsync(offer, "Applying", null, cancellationToken).ConfigureAwait(false);
        }

        // The longest an answer can take: a health check, and a provisioning run.
        var deadline = _clock.UtcNow + TimeSpan.FromSeconds(request.HealthTimeoutSeconds) + TimeSpan.FromMinutes(25);
        while (_clock.UtcNow < deadline)
        {
            if (AgentFiles.Read<AgentResult>(ResultPath(request.Id)) is { } result)
            {
                _host.SetUpdating(false);
                _host.SetDraining(false);
                await ReportResultAsync(result, offer?.ReleaseId, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _delay(ResultPoll, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError("The agent did not answer request {Id} ({Kind}).", request.Id, request.Kind);
        _host.SetUpdating(false);
        _host.SetDraining(false);
        var timedOut = new AgentResult(request.Id, request.Kind, AgentOutcome.Failed, _clock.UtcNow, request.CommandId,
            offer?.Component, offer?.Version, null, "updates.agentNotAnswering");
        await ReportResultAsync(timedOut, offer?.ReleaseId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The update window (unless <paramref name="windowFree"/>) and a kiosk with
    /// no customer. After <c>Update.MaxIdleWaitMinutes</c> of waiting, new
    /// customers are turned away and the one there finishes (layihə sahibinin
    /// qərarı, DECISIONS.md 2026-10-25).
    /// </summary>
    /// <returns>False when the package is not offered any more: nothing is applied.</returns>
    private async Task<bool> WaitForMomentAsync(KioskReleaseOffer? offer, bool windowFree, CancellationToken cancellationToken)
    {
        DateTime? waitingSince = null;
        var draining = false;
        var asked = _clock.UtcNow;

        while (true)
        {
            var now = _clock.UtcNow;
            var settings = Settings;
            windowFree |= _hurry;

            // Asked again now and then, and when the hub says something changed:
            // a withdrawn package is not applied, one made urgent stops waiting for the window.
            if (offer is not null && (_recheck || now - asked > OfferRecheck))
            {
                _recheck = false;
                asked = now;
                if (await StillOfferedAsync(offer, cancellationToken).ConfigureAwait(false) is { } still)
                {
                    if (still is false)
                    {
                        _logger.LogInformation("{Component} {Version} is not offered any more; it is not applied.", offer.Component, offer.Version);
                        _host.SetDraining(false);
                        if (_hurryCommand is { } command)
                        {
                            _hurry = false;
                            _hurryCommand = null;
                            await _api.CompleteCommandAsync(command, true, "updates.result.nothing", 0, cancellationToken).ConfigureAwait(false);
                        }

                        return false;
                    }

                    windowFree |= _urgentNow;
                }
            }

            if (windowFree || UpdateWindow.Parse(settings.Window).Contains(_localTime(now)))
            {
                if (waitingSince is null)
                {
                    waitingSince = now;
                    if (offer is not null)
                    {
                        await ReportAsync(offer, "WaitingForIdle", null, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (_host.IsIdle)
                {
                    return true;
                }

                if (!draining && now - waitingSince > TimeSpan.FromMinutes(Math.Clamp(settings.MaxIdleWaitMinutes, 1, 24 * 60)))
                {
                    _logger.LogInformation("No idle moment in {Minutes} minutes: new customers are turned away until the update.", settings.MaxIdleWaitMinutes);
                    draining = true;
                    _host.SetDraining(true);
                    if (offer is not null)
                    {
                        await ReportAsync(offer, "Draining", null, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else if (draining)
            {
                // The window closed while turning customers away: take them again, try at the next window.
                draining = false;
                waitingSince = null;
                _host.SetDraining(false);
            }

            await _delay(MomentPoll, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool _urgentNow;

    /// <returns>Whether the package is still offered; null when the backend could not be asked.</returns>
    private async Task<bool?> StillOfferedAsync(KioskReleaseOffer offer, CancellationToken cancellationToken)
    {
        try
        {
            var offers = await _api.GetUpdateOffersAsync(cancellationToken).ConfigureAwait(false);
            var same = offers.Offers.FirstOrDefault(item => item.ReleaseId == offer.ReleaseId);
            _urgentNow = same?.Urgent ?? false;
            return same is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
        {
            _logger.LogWarning(exception, "Whether {Component} {Version} is still offered could not be asked.", offer.Component, offer.Version);
            return null;
        }
    }

    private async Task ReportResultsLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReportResultsAsync(cancellationToken).ConfigureAwait(false);
                await _delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is not DeviceRevokedException)
            {
                _logger.LogWarning(exception, "The agent's answers could not be read.");
            }
        }
    }

    /// <summary>The agent's answers this process has not passed on yet (a switch of the UI is answered here).</summary>
    public async Task ReportResultsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.Results))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_paths.Results, "*.json"))
        {
            if (AgentFiles.Read<AgentResult>(file) is { } result && !IsReported(result.Id))
            {
                try
                {
                    await ReportResultAsync(result, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
                {
                    _logger.LogWarning(exception, "The agent's answer {Id} could not be passed on yet.", result.Id);
                }
            }
        }
    }

    private async Task ReportResultAsync(AgentResult result, Guid? releaseId, CancellationToken cancellationToken)
    {
        if (result.Outcome == AgentOutcome.Refused && releaseId is { } refused)
        {
            _refused.Add(refused);
        }

        // "Not newer" is the agent having it already - nothing went wrong, nothing to report.
        // Nor is a version it took back before: the backend heard that with the rollback.
        var nothingToDo = result.Outcome == AgentOutcome.Refused && result.MessageKey is "updates.notNewer" or "updates.refused.failedHere";
        if (result.Component is { } component && result.Version is { } version && result.Kind != AgentRequestKind.RestartAgent && !nothingToDo)
        {
            var status = result.Outcome switch
            {
                AgentOutcome.Succeeded => "Succeeded",
                AgentOutcome.RolledBack => "RolledBack",
                _ => "Failed",
            };
            await _api.ReportUpdateStatusAsync(component, version, status, result.MessageKey, cancellationToken).ConfigureAwait(false);
        }

        if (result.CommandId is { } commandId)
        {
            var (succeeded, messageKey, _) = Answer(result);
            await _api.CompleteCommandAsync(commandId, succeeded, messageKey, 0, cancellationToken).ConfigureAwait(false);
        }

        MarkReported(result.Id);
    }

    private static (bool, string, int) Answer(AgentResult result) => result.Outcome switch
    {
        AgentOutcome.Succeeded => (true, result.Kind switch
        {
            AgentRequestKind.RestartAgent => "kioskCommands.agentRestarted",
            AgentRequestKind.ReapplyProvisioning => "kioskCommands.provisioningApplied",
            _ => "updates.installed",
        }, 0),
        AgentOutcome.RolledBack when result.MessageKey == "updates.rolledBackByAdmin" => (true, "kioskCommands.rolledBack", 0),
        _ => (false, result.MessageKey ?? "updates.reasonUnknown", 0),
    };

    private async Task ReportAsync(KioskReleaseOffer offer, string status, string? messageKey, CancellationToken cancellationToken)
    {
        try
        {
            await _api.ReportUpdateStatusAsync(offer.Component, offer.Version, status, messageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
        {
            // The update goes on; the panel hears at the next step.
            _logger.LogWarning(exception, "Status {Status} of {Component} {Version} could not be reported.", status, offer.Component, offer.Version);
        }
    }

    private string ResultPath(Guid id) => Path.Combine(_paths.Results, $"{id:N}.json");

    private string ReportedPath(Guid id) => Path.Combine(_paths.UpdatesRoot, "reported", id.ToString("N"));

    private bool IsReported(Guid id) => File.Exists(ReportedPath(id));

    private void MarkReported(Guid id)
    {
        Directory.CreateDirectory(Path.Combine(_paths.UpdatesRoot, "reported"));
        File.WriteAllText(ReportedPath(id), string.Empty);
    }
}
