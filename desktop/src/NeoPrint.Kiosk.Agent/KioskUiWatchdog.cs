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
/// will not fix, so after <see cref="AgentOptions.CrashLoopThreshold"/> the
/// kiosk is put into safe mode rather than looped forever. Safe mode itself,
/// the updater and the remote commands land in step 3.
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
            try
            {
                if (IsCrashLooping)
                {
                    _logger.LogCritical(
                        "The kiosk UI has exited {Count} times within {Minutes} minutes; not restarting it again. " +
                        "The kiosk needs attention on site.",
                        _recentExits.Count, CrashWindow.TotalMinutes);

                    return;
                }

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
                await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, _options.RestartDelaySeconds)), stoppingToken)
                    .ConfigureAwait(false);
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

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // A clean exit is still an exit: on a kiosk there is nothing for the
        // customer to look at, so it counts towards the crash loop either way.
        _logger.LogWarning("The kiosk UI exited with code {ExitCode}; restarting.", exitCode);
        RecordExit(DateTimeOffset.UtcNow);
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
