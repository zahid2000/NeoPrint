using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// The provisioning scripts, which run as root: installed and run by the
/// root-owned helper (<see cref="RootProvisioning"/>), never by the agent itself.
/// </summary>
public interface IProvisioningRunner
{
    /// <summary>A checked package to install and run in repair mode; the helper checks it again.</summary>
    /// <returns>0 done, 2 refused, anything else failed.</returns>
    Task<int> InstallAsync(string package, string manifestPath, string signaturePath, CancellationToken cancellationToken);

    /// <summary>The current scripts, again (<c>ReapplyProvisioning</c>).</summary>
    Task<int> ReapplyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The agent's side of an update (docs/KIOSK_UPDATES.md; T-321): takes the
/// UI's requests, decides on each one itself, switches versions, waits for the
/// health check, rolls back - and after a power cut finishes or undoes a
/// switch that was cut short.
/// </summary>
/// <remarks>
/// <para>
/// The UI is trusted with nothing. A request names a file in the downloads
/// folder, which the UI can write to; the agent copies it into its own folder
/// first and checks only that copy - signature, hash, key set, version rules -
/// so the file cannot change between the check and the install. Component and
/// version come from the signed manifest, not from the request.
/// </para>
/// <para>
/// Every step of a switch is written to the journal before it is taken. The
/// previous version stays on disk until the new one has passed its health
/// check.
/// </para>
/// </remarks>
public sealed partial class UpdateApplier
{
    private const int MaxSmallFile = 16 * 1024;
    private const long MaxPackage = 512L * 1024 * 1024;
    private const long DiskMargin = 200L * 1024 * 1024;

    /// <summary>How long a new agent runs before it is taken to have started well.</summary>
    public static readonly TimeSpan AgentProvesItself = TimeSpan.FromSeconds(30);

    private readonly UpdatePaths _paths;
    private readonly ReleasePackageCheck _check;
    private readonly IUiHost _ui;
    private readonly IProvisioningRunner _provisioning;
    private readonly IClock _clock;
    private readonly ILogger<UpdateApplier> _logger;
    private readonly string _agentVersion;
    private readonly Action _restartAgent;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _platformUpdatesItself;
    private readonly string _platform;

    private AgentState _state = new();

    /// <param name="restartAgent">Ends the agent process; the service manager starts it again from <c>agent/current</c>.</param>
    /// <param name="platformUpdatesItself">Whether agent and provisioning updates are supported here (not on Windows yet, T-093).</param>
    public UpdateApplier(
        UpdatePaths paths,
        ReleasePackageCheck check,
        IUiHost ui,
        IProvisioningRunner provisioning,
        IClock clock,
        ILogger<UpdateApplier> logger,
        string agentVersion,
        Action restartAgent,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool? platformUpdatesItself = null,
        string? platform = null)
    {
        _paths = paths;
        _check = check;
        _ui = ui;
        _provisioning = provisioning;
        _clock = clock;
        _logger = logger;
        _agentVersion = agentVersion;
        _restartAgent = restartAgent;
        _delay = delay ?? Task.Delay;
        _platformUpdatesItself = platformUpdatesItself ?? !OperatingSystem.IsWindows();
        _platform = platform ?? UpdatePaths.Platform;
    }

    /// <summary>What the agent knows about the machine.</summary>
    public AgentState State => _state;

    /// <summary>For the tests: called once the agent has its own copy, before the check.</summary>
    internal Action<string>? AfterCopy { get; init; }

    /// <summary>How often a health marker or a request is looked for.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,199}$")]
    private static partial Regex FileName();

    private VersionLinks Links(string component) => new(_paths.Versions(component), _paths.Current(component));

    // ---------------------------------------------------------------------
    // Start
    // ---------------------------------------------------------------------

    /// <summary>At the agent's start: the folders, the state, and whatever a power cut left half done.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var folder in new[] { _paths.Results, _paths.Incoming, _paths.Downloads, _paths.Requests, _paths.Health })
        {
            Directory.CreateDirectory(folder);
        }

        if (!OperatingSystem.IsWindows())
        {
            // The agent's own copy of a package: nobody else reads it, let alone writes it.
            File.SetUnixFileMode(_paths.Incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // What runs is what "current" points at - the state file may be older
        // than the folders (a machine set up again, a technician's install).
        var state = AgentFiles.Read<AgentState>(_paths.StateFile) ?? new AgentState();
        var installed = new Dictionary<string, string>(state.Installed);
        foreach (var component in new[] { UpdateComponents.App, UpdateComponents.Provisioning })
        {
            if (Links(component).CurrentVersion() is { } current)
            {
                installed[component] = current;
            }
        }

        _state = state with { AgentVersion = _agentVersion, AgentStartedAt = _clock.UtcNow, Installed = installed, Busy = null };
        SaveState();

        foreach (var partial in new[] { UpdateComponents.App, UpdateComponents.Agent, UpdateComponents.Provisioning })
        {
            Links(partial).Prune(int.MaxValue);
        }

        await RecoverAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (AgentFiles.Read<AgentJournal>(_paths.JournalFile) is not { } journal)
        {
            // Nothing half done: the agent is whatever is running.
            if (_state.Installed.GetValueOrDefault(UpdateComponents.Agent) != _agentVersion)
            {
                SetInstalled(UpdateComponents.Agent, _agentVersion, previous: null);
            }

            return;
        }

        _logger.LogWarning("A {Component} switch {From} -> {To} was cut short at {Phase}; finishing it.", journal.Component, journal.From, journal.To, journal.Phase);

        switch (journal.Component)
        {
            case UpdateComponents.Agent:
                await RecoverAgentAsync(journal, cancellationToken).ConfigureAwait(false);
                return;

            default:
                await RecoverAppAsync(journal, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private async Task RecoverAppAsync(AgentJournal journal, CancellationToken cancellationToken)
    {
        var links = Links(UpdateComponents.App);
        var current = links.CurrentVersion();

        if (journal.Phase == JournalPhase.Switching && current != journal.To)
        {
            // The switch itself did not happen: the old version runs on.
            if (current is null && Directory.Exists(links.VersionFolder(journal.From)))
            {
                links.SwitchTo(journal.From);
            }

            await _ui.StartAsync(cancellationToken).ConfigureAwait(false);
            Finish(journal, AgentOutcome.Failed, journal.To, "updates.interrupted");
            return;
        }

        // The new version is current: its health check starts over, with all its time.
        var timeout = journal.HealthDeadline - journal.StartedAt;
        var resumed = journal with { Phase = JournalPhase.HealthCheck, HealthDeadline = _clock.UtcNow + Clamp(timeout) };
        AgentFiles.Write(_paths.JournalFile, resumed);
        await _ui.StartAsync(cancellationToken).ConfigureAwait(false);
        await HealthCheckAsync(resumed, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAgentAsync(AgentJournal journal, CancellationToken cancellationToken)
    {
        if (_agentVersion == journal.To)
        {
            // The new agent is the one running: it has started; give it its half minute.
            await _delay(AgentProvesItself, cancellationToken).ConfigureAwait(false);
            File.Delete(_paths.AgentPendingFile);
            SetInstalled(UpdateComponents.Agent, journal.To, journal.From);
            Links(UpdateComponents.Agent).Prune(KeepOf(journal), journal.To, journal.From);
            Finish(journal, AgentOutcome.Succeeded, journal.To, null);
            return;
        }

        // The guard put the previous agent back: the new one could not start.
        File.Delete(_paths.AgentPendingFile);
        MarkFailedHere(UpdateComponents.Agent, journal.To);
        SetInstalled(UpdateComponents.Agent, _agentVersion, previous: null);
        Finish(journal, AgentOutcome.RolledBack, journal.To, "updates.agentStartFailed");
    }

    // ---------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------

    /// <summary>Carries out the UI's requests, oldest first; one already answered is only cleared away.</summary>
    /// <returns>How many were answered.</returns>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var answered = 0;
        foreach (var file in Directory.GetFiles(_paths.Requests, "*.json").OrderBy(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (AgentFiles.Read<AgentRequest>(file) is not { } request)
            {
                _logger.LogWarning("A request the agent cannot read was set aside: {File}", Path.GetFileName(file));
                File.Delete(file);
                continue;
            }

            if (!File.Exists(ResultPath(request.Id)))
            {
                var result = await ProcessAsync(request, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    // On its way down (the agent's own update or restart): the next agent answers.
                    return answered;
                }

                answered++;
            }

            File.Delete(file);
        }

        return answered;
    }

    /// <returns>The answer; null when the agent is restarting and its successor answers.</returns>
    public async Task<AgentResult?> ProcessAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _state = _state with { Busy = request.Id };
        SaveState();

        try
        {
            return request.Kind switch
            {
                AgentRequestKind.Install => await InstallAsync(request, cancellationToken).ConfigureAwait(false),
                AgentRequestKind.Rollback => await RollbackAsync(request, cancellationToken).ConfigureAwait(false),
                AgentRequestKind.ReapplyProvisioning => await ReapplyProvisioningAsync(request, cancellationToken).ConfigureAwait(false),
                AgentRequestKind.RestartAgent => RestartAgent(request),
                _ => Answer(request, AgentOutcome.Failed, null, null, null, "updates.reasonUnknown"),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Request {Id} ({Kind}) failed.", request.Id, request.Kind);
            return Answer(request, AgentOutcome.Failed, null, null, null, "updates.reasonUnknown");
        }
        finally
        {
            _state = _state with { Busy = null };
            SaveState();
        }
    }

    private async Task<AgentResult?> InstallAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        if (request.PackageFile is not { } fileName || !FileName().IsMatch(fileName)
            || request.Manifest is not { } manifestText || request.Signature is not { } signatureText
            || Encoding.UTF8.GetByteCount(manifestText) > MaxSmallFile || Encoding.UTF8.GetByteCount(signatureText) > MaxSmallFile)
        {
            return Answer(request, AgentOutcome.Refused, null, null, null, "updates.refused.malformedManifest");
        }

        var manifestBytes = Encoding.UTF8.GetBytes(manifestText);
        var signatureBytes = Encoding.UTF8.GetBytes(signatureText);

        ReleaseManifest claimed;
        try
        {
            claimed = ReleaseManifest.Parse(manifestBytes);
        }
        catch (FormatException)
        {
            return Answer(request, AgentOutcome.Refused, null, null, null, "updates.refused.malformedManifest");
        }

        var component = claimed.Component.ToString();
        var version = claimed.Version.ToString();
        var installed = _state.Installed.GetValueOrDefault(component);

        if (component != UpdateComponents.App && !_platformUpdatesItself)
        {
            return Answer(request, AgentOutcome.Refused, component, version, installed, "updates.notSupportedHere");
        }

        // Withdrawn packages: what the UI passes on is only ever added.
        var withdrawn = _state.Withdrawn.Union(request.Withdrawn ?? [], StringComparer.OrdinalIgnoreCase).ToList();
        _state = _state with { Withdrawn = withdrawn };
        SaveState();

        var key = $"{component}@{version}";
        if (request.Retry)
        {
            _state = _state with { FailedHere = [.. _state.FailedHere.Where(failed => failed != key)] };
        }
        else if (_state.FailedHere.Contains(key))
        {
            return Answer(request, AgentOutcome.Refused, component, version, installed, "updates.refused.failedHere");
        }

        // The agent's own copy, in its own folder: the check and the install see the same bytes.
        var source = Path.Combine(_paths.Downloads, fileName);
        var copy = Path.Combine(_paths.Incoming, $"{request.Id:N}.pkg");
        if (!await CopyAsync(source, copy, cancellationToken).ConfigureAwait(false))
        {
            return Answer(request, AgentOutcome.Failed, component, version, installed, "updates.downloadFailed");
        }

        AfterCopy?.Invoke(source);

        try
        {
            var check = await _check.CheckAsync(
                    manifestBytes,
                    signatureBytes,
                    copy,
                    new ReleaseExpectation(
                        claimed.Component,
                        _platform,
                        _check.KeySet,
                        installed is not null && ReleaseVersion.TryParse(installed, out var running) ? running : null,
                        ReleaseVersion.TryParse(_agentVersion, out var agent) ? agent : ReleaseVersion.Parse("0.0.0"),
                        withdrawn),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!check.Accepted)
            {
                _logger.LogWarning("Package {Component} {Version} refused: {Rejection} - {Detail}", component, version, check.Rejection, check.Detail);
                return Answer(request, AgentOutcome.Refused, component, version, installed, ReleasePackageCheck.MessageKey(check.Rejection));
            }

            if (component == UpdateComponents.Provisioning)
            {
                return await InstallProvisioningAsync(request, copy, manifestBytes, signatureBytes, installed, version, cancellationToken).ConfigureAwait(false);
            }

            var links = Links(component);
            if (!HasRoomFor(new FileInfo(copy).Length))
            {
                return Answer(request, AgentOutcome.Failed, component, version, installed, "updates.noDiskSpace");
            }

            try
            {
                await UnpackAsync(links, copy, version, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                _logger.LogError(exception, "Package {Component} {Version} could not be unpacked.", component, version);
                return Answer(request, AgentOutcome.Failed, component, version, installed, "updates.badArchive");
            }
        }
        finally
        {
            File.Delete(copy);
        }

        var from = installed ?? Links(component).CurrentVersion() ?? string.Empty;
        return component == UpdateComponents.Agent
            ? SwitchAgent(request, from, version)
            : await SwitchAppAsync(request, from, version, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentResult> RollbackAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var links = Links(UpdateComponents.App);
        var current = links.CurrentVersion() ?? _state.Installed.GetValueOrDefault(UpdateComponents.App);
        if (current is null || _state.Previous.GetValueOrDefault(UpdateComponents.App) is not { } previous
            || previous == current || !links.IsComplete(previous))
        {
            return Answer(request, AgentOutcome.Failed, UpdateComponents.App, current, current, "updates.nothingToRollBack");
        }

        return await SwitchAppAsync(request, current, previous, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentResult> ReapplyProvisioningAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var current = Links(UpdateComponents.Provisioning).CurrentVersion();
        if (!_platformUpdatesItself || current is null)
        {
            return Answer(request, AgentOutcome.Failed, UpdateComponents.Provisioning, current, current,
                _platformUpdatesItself ? "updates.nothingToRollBack" : "updates.notSupportedHere");
        }

        var code = await _provisioning.ReapplyAsync(cancellationToken).ConfigureAwait(false);
        return code == 0
            ? Answer(request, AgentOutcome.Succeeded, UpdateComponents.Provisioning, current, current, null)
            : Answer(request, AgentOutcome.Failed, UpdateComponents.Provisioning, current, current, "updates.provisioningFailed");
    }

    private AgentResult? RestartAgent(AgentRequest request)
    {
        var result = Answer(request, AgentOutcome.Succeeded, UpdateComponents.Agent, _agentVersion, _agentVersion, null);
        _restartAgent();
        return result;
    }

    // ---------------------------------------------------------------------
    // Switching
    // ---------------------------------------------------------------------

    private async Task<AgentResult> SwitchAppAsync(AgentRequest request, string from, string to, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var timeout = Clamp(TimeSpan.FromSeconds(request.HealthTimeoutSeconds));
        var journal = new AgentJournal(request.Id, request.Kind, UpdateComponents.App, from, to, JournalPhase.Switching, now, now + timeout,
            request.CommandId, Math.Clamp(request.KeepVersions, 2, 10));
        AgentFiles.Write(_paths.JournalFile, journal);

        _logger.LogInformation("Switching the kiosk UI {From} -> {To}.", from, to);
        await _ui.StopAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            File.Delete(_paths.HealthMarker(UpdateComponents.App, to));
            Links(UpdateComponents.App).SwitchTo(to);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The switch did not happen: the old version runs on, and the kiosk is never left without a UI.
            _logger.LogError(exception, "The kiosk UI could not be switched to {To}; {From} runs on.", to, from);
            var links = Links(UpdateComponents.App);
            if (links.CurrentVersion() != from && Directory.Exists(links.VersionFolder(from)))
            {
                links.SwitchTo(from);
            }

            await _ui.StartAsync(cancellationToken).ConfigureAwait(false);
            return Finish(journal, AgentOutcome.Failed, to, "updates.reasonUnknown");
        }

        journal = journal with { Phase = JournalPhase.HealthCheck, HealthDeadline = _clock.UtcNow + timeout };
        AgentFiles.Write(_paths.JournalFile, journal);
        await _ui.StartAsync(cancellationToken).ConfigureAwait(false);

        return await HealthCheckAsync(journal, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentResult> HealthCheckAsync(AgentJournal journal, CancellationToken cancellationToken)
    {
        var marker = _paths.HealthMarker(UpdateComponents.App, journal.To);
        while (_clock.UtcNow < journal.HealthDeadline)
        {
            if (File.Exists(marker))
            {
                _logger.LogInformation("Kiosk UI {Version} passed its health check.", journal.To);
                var keep = KeepOf(journal);
                SetInstalled(UpdateComponents.App, journal.To, journal.From);
                Links(UpdateComponents.App).Prune(keep, journal.To, journal.From);

                // An administrator's rollback: the version left behind is "taken back", by them.
                return journal.Kind == AgentRequestKind.Rollback
                    ? Finish(journal, AgentOutcome.RolledBack, journal.From, "updates.rolledBackByAdmin")
                    : Finish(journal, AgentOutcome.Succeeded, journal.To, null);
            }

            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError("Kiosk UI {Version} did not pass its health check in time; going back to {From}.", journal.To, journal.From);
        await _ui.StopAsync(cancellationToken).ConfigureAwait(false);
        var links = Links(UpdateComponents.App);
        if (Directory.Exists(links.VersionFolder(journal.From)))
        {
            links.SwitchTo(journal.From);
        }

        await _ui.StartAsync(cancellationToken).ConfigureAwait(false);
        _state = _state with { Installed = new Dictionary<string, string>(_state.Installed) { [UpdateComponents.App] = journal.From } };
        SaveState();

        if (journal.Kind == AgentRequestKind.Rollback)
        {
            // Going back did not work: the newer version runs again.
            return Finish(journal, AgentOutcome.Failed, journal.To, "updates.healthTimeout");
        }

        MarkFailedHere(UpdateComponents.App, journal.To);
        return Finish(journal, AgentOutcome.RolledBack, journal.To, "updates.healthTimeout");
    }

    private AgentResult? SwitchAgent(AgentRequest request, string from, string to)
    {
        var now = _clock.UtcNow;
        var journal = new AgentJournal(request.Id, request.Kind, UpdateComponents.Agent, from, to, JournalPhase.HealthCheck, now,
            now + AgentProvesItself, request.CommandId, Math.Clamp(request.KeepVersions, 2, 10));
        AgentFiles.Write(_paths.JournalFile, journal);

        // For the guard outside this package: three starts of the new agent, then the old one again.
        File.WriteAllText(_paths.AgentPendingFile, $"{from} {to} 0\n");
        Links(UpdateComponents.Agent).SwitchTo(to);

        _logger.LogInformation("The agent switches itself {From} -> {To} and restarts.", from, to);
        _restartAgent();
        return null;
    }

    /// <summary>
    /// Handed to the root-owned helper, which checks the package again with its
    /// own keys, unpacks it, switches and runs it - and goes back if it fails.
    /// </summary>
    private async Task<AgentResult> InstallProvisioningAsync(
        AgentRequest request, string copy, byte[] manifest, byte[] signature, string? from, string version, CancellationToken cancellationToken)
    {
        var manifestPath = copy + ".manifest.json";
        var signaturePath = copy + ".manifest.json.sig";
        await File.WriteAllBytesAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(signaturePath, signature, cancellationToken).ConfigureAwait(false);

        try
        {
            var code = await _provisioning.InstallAsync(copy, manifestPath, signaturePath, cancellationToken).ConfigureAwait(false);
            if (code == 0 && Links(UpdateComponents.Provisioning).CurrentVersion() == version)
            {
                SetInstalled(UpdateComponents.Provisioning, version, from);
                return Answer(request, AgentOutcome.Succeeded, UpdateComponents.Provisioning, version, from, null);
            }

            _logger.LogError("Provisioning {Version} ended with {Code}.", version, code);
            return code == 2
                ? Answer(request, AgentOutcome.Refused, UpdateComponents.Provisioning, version, from, "updates.signatureRefused")
                : Answer(request, AgentOutcome.Failed, UpdateComponents.Provisioning, version, from, "updates.provisioningFailed");
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(signaturePath);
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task UnpackAsync(VersionLinks links, string package, string version, CancellationToken cancellationToken)
    {
        var target = links.VersionFolder(version);
        if (links.IsComplete(version))
        {
            return;
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        var partial = target + ".partial";
        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        try
        {
            await PackageArchive.ExtractAsync(package, partial, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Nothing half-unpacked is left behind.
            Directory.Delete(partial, recursive: true);
            throw;
        }

        // The program itself runs whatever bits the archive carried: a package
        // packed where files have no execute bit would otherwise never start.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var program in new[] { "NeoPrint.Kiosk", "NeoPrint.Kiosk.Agent", "createdump" })
            {
                var path = Path.Combine(partial, program);
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(partial, VersionLinks.CompleteMarker), version, cancellationToken).ConfigureAwait(false);
        Directory.Move(partial, target);
    }

    private static async Task<bool> CopyAsync(string source, string copy, CancellationToken cancellationToken)
    {
        if (!File.Exists(source) || new FileInfo(source).Length > MaxPackage)
        {
            return false;
        }

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using var output = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private bool HasRoomFor(long packageSize)
    {
        try
        {
            Directory.CreateDirectory(_paths.InstallRoot);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_paths.InstallRoot))!);
            return drive.AvailableFreeSpace >= (packageSize * 3) + DiskMargin;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown is not "full": the unpacking itself fails cleanly if it is.
            return true;
        }
    }

    private static TimeSpan Clamp(TimeSpan timeout)
        => TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, 30, 30 * 60));

    private static int KeepOf(AgentJournal journal) => Math.Clamp(journal.KeepVersions, 2, 10);

    private void SetInstalled(string component, string version, string? previous)
    {
        var installed = new Dictionary<string, string>(_state.Installed) { [component] = version };
        var before = new Dictionary<string, string>(_state.Previous);
        if (!string.IsNullOrEmpty(previous) && previous != version)
        {
            before[component] = previous;
        }

        _state = _state with { Installed = installed, Previous = before };
        SaveState();
    }

    private void MarkFailedHere(string component, string version)
    {
        var key = $"{component}@{version}";
        if (!_state.FailedHere.Contains(key))
        {
            _state = _state with { FailedHere = [.. _state.FailedHere, key] };
            SaveState();
        }
    }

    private AgentResult Finish(AgentJournal journal, AgentOutcome outcome, string version, string? messageKey)
    {
        var result = new AgentResult(journal.RequestId, journal.Kind, outcome, _clock.UtcNow, journal.CommandId,
            journal.Component, version, journal.From, messageKey);
        AgentFiles.Write(ResultPath(journal.RequestId), result);
        File.Delete(_paths.JournalFile);
        _logger.LogInformation("Request {Id}: {Outcome} {Component} {Version} ({Message}).", journal.RequestId, outcome, journal.Component, version, messageKey);
        return result;
    }

    private AgentResult Answer(AgentRequest request, AgentOutcome outcome, string? component, string? version, string? from, string? messageKey)
    {
        var result = new AgentResult(request.Id, request.Kind, outcome, _clock.UtcNow, request.CommandId, component, version, from, messageKey);
        AgentFiles.Write(ResultPath(request.Id), result);
        _logger.LogInformation("Request {Id}: {Outcome} {Component} {Version} ({Message}).", request.Id, outcome, component, version, messageKey);
        return result;
    }

    private string ResultPath(Guid id) => Path.Combine(_paths.Results, $"{id:N}.json");

    private void SaveState() => AgentFiles.Write(_paths.StateFile, _state);
}
