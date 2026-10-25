using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NeoPrint.Kiosk.Agent;

/// <summary>
/// How the agent is configured (plan section 7.12).
/// </summary>
/// <remarks>
/// No secrets: the agent, like the UI, carries no credentials. Anything about
/// a particular kiosk comes from the backend after activation.
/// </remarks>
public sealed class AgentOptions
{
    /// <summary>
    /// The UI executable. Empty means the agent does not launch it, which is
    /// the case on Windows, where Shell Launcher owns the UI's lifetime.
    /// </summary>
    public string UiExecutablePath { get; set; } = string.Empty;

    /// <summary>How long to wait before restarting a UI that exited.</summary>
    public int RestartDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Crashes within ten minutes that put the kiosk into safe mode
    /// (<c>Kiosk.CrashLoopThreshold</c>, plan section 2.4).
    /// </summary>
    public int CrashLoopThreshold { get; set; } = 3;

    /// <summary>How often the watchdog looks at the UI.</summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// The wait before each restart once the UI is crash-looping: long enough
    /// not to spin the machine, short enough that the safe-mode screen is up
    /// within a minute.
    /// </summary>
    public int CrashLoopRestartDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Who runs the UI, so the updater can stop and start it (T-321):
    /// <c>watchdog</c> (this agent, with <see cref="UiExecutablePath"/>),
    /// <c>systemd</c> (a provisioned Linux kiosk), <c>shell-launcher</c>
    /// (Windows). Empty: worked out from the platform and the path.
    /// </summary>
    public string UiHost { get; set; } = string.Empty;

    /// <summary>
    /// The root-owned helper that installs and runs the provisioning scripts
    /// (<c>RootProvisioning</c>); empty means this agent does it itself - only
    /// where it runs as root, as in the container kiosk.
    /// </summary>
    public string ProvisioningHelper { get; set; } = "sudo -n /usr/local/lib/neoprint/neoprint-reprovision";

    /// <summary>What the scripts are run with: <c>--repair</c>, or <c>--dry-run</c> where they must change nothing.</summary>
    public string ProvisioningScriptArgs { get; set; } = "--repair";

    /// <summary>Overrides of the provisioned folders (docs/KIOSK_UPDATES.md section 2); empty means the platform's.</summary>
    public string InstallRoot { get; set; } = string.Empty;

    public string UpdatesRoot { get; set; } = string.Empty;

    public string AgentRoot { get; set; } = string.Empty;

    /// <summary>Where <c>agent-YYYYMMDD.log</c> goes; empty means the platform's log folder.</summary>
    public string LogDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Keeps the kiosk UI running (plan section 7.12).
/// </summary>
/// <remarks>
/// <para>
/// On Linux the agent launches the UI and restarts it if it exits. On Windows
/// Shell Launcher already does that, so the agent only watches and counts: a
/// second restarter would fight the first.
/// </para>
/// <para>
/// Repeated crashes in a short window mean something is wrong that restarting
/// quickly will not fix. The UI counts its own crashes and starts in safe mode
/// ("Texniki xidmət", no orders) after <see cref="AgentOptions.CrashLoopThreshold"/>
/// of them (plan section 7.12); the agent keeps restarting it, only more
/// slowly, so the kiosk always ends up showing that screen rather than a bare
/// desktop. The updater and the remote commands are plan step 7.
/// </para>
/// </remarks>
public sealed class KioskUiWatchdog : BackgroundService
{
    /// <summary>The window over which crashes are counted.</summary>
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    private readonly AgentOptions _options;
    private readonly ILogger<KioskUiWatchdog> _logger;
    private readonly Queue<DateTimeOffset> _recentExits = new();

    private Process? _ui;

    // Set while the updater swaps versions: the UI is down on purpose, and is
    // not started again until it is released (T-321).
    private volatile bool _held;
    private TaskCompletionSource _released = NewReleased();

    private static TaskCompletionSource NewReleased() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public KioskUiWatchdog(IOptions<AgentOptions> options, ILogger<KioskUiWatchdog> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Exits counted inside the crash window, exposed for the tests.</summary>
    public int RecentExitCount => _recentExits.Count;

    /// <summary>Whether the crash loop threshold has been reached.</summary>
    public bool IsCrashLooping => _recentExits.Count >= _options.CrashLoopThreshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.UiExecutablePath))
        {
            // Windows: Shell Launcher owns the UI. Nothing to launch here, and
            // launching anyway would give the kiosk two UIs.
            _logger.LogInformation(
                "No UI executable is configured; the platform's shell manager owns the UI's lifetime.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_held)
            {
                await _released.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunUiAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Starting the kiosk UI failed.");
                RecordExit(DateTimeOffset.UtcNow);
            }

            try
            {
                await Task.Delay(RestartDelay(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunUiAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting the kiosk UI: {Path}", _options.UiExecutablePath);

        _ui = Process.Start(new ProcessStartInfo
        {
            FileName = _options.UiExecutablePath,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException(
            $"Could not start the kiosk UI at '{_options.UiExecutablePath}'.");

        await _ui.WaitForExitAsync(stoppingToken).ConfigureAwait(false);

        var exitCode = _ui.ExitCode;
        _ui.Dispose();
        _ui = null;

        if (stoppingToken.IsCancellationRequested || _held)
        {
            return;
        }

        // A clean exit is still an exit: on a kiosk there is nothing for the
        // customer to look at, so it counts towards the crash loop either way.
        _logger.LogWarning("The kiosk UI exited with code {ExitCode}; restarting.", exitCode);
        RecordExit(DateTimeOffset.UtcNow);
    }

    /// <summary>Stops the UI and keeps it stopped until <see cref="Release"/> (the updater's switch).</summary>
    public async Task HoldAsync(CancellationToken cancellationToken)
    {
        _released = NewReleased();
        _held = true;

        if (_ui is { HasExited: false } ui)
        {
            _logger.LogInformation("Stopping the kiosk UI for an update.");
            try
            {
                ui.Kill(entireProcessTree: true);
                await ui.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Lets the UI start again - from whatever <c>current</c> now points at.</summary>
    public void Release()
    {
        _held = false;
        _recentExits.Clear();
        _released.TrySetResult();
    }

    /// <summary>How long to wait before the next start: longer while the UI is crash-looping.</summary>
    internal TimeSpan RestartDelay()
    {
        if (!IsCrashLooping)
        {
            return TimeSpan.FromSeconds(Math.Max(1, _options.RestartDelaySeconds));
        }

        _logger.LogCritical(
            "The kiosk UI has exited {Count} times within {Minutes} minutes; it restarts in safe mode, more slowly.",
            _recentExits.Count, CrashWindow.TotalMinutes);

        return TimeSpan.FromSeconds(Math.Max(_options.RestartDelaySeconds, _options.CrashLoopRestartDelaySeconds));
    }

    /// <summary>
    /// Records an exit and drops the ones that fell out of the window.
    /// </summary>
    /// <remarks>
    /// Internal so the tests can drive the crash loop without launching
    /// processes.
    /// </remarks>
    internal void RecordExit(DateTimeOffset at)
    {
        _recentExits.Enqueue(at);

        while (_recentExits.Count > 0 && at - _recentExits.Peek() > CrashWindow)
        {
            _recentExits.Dequeue();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_ui is { HasExited: false })
        {
            _logger.LogInformation("Stopping the kiosk UI.");

            try
            {
                _ui.Kill(entireProcessTree: true);
                await _ui.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Already gone.
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
