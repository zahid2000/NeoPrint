using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>Stops the kiosk UI for a version switch and starts it again afterwards (T-321).</summary>
public interface IUiHost
{
    Task StopAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>The agent launched the UI itself (the Linux container kiosk; <c>Agent:UiExecutablePath</c>).</summary>
public sealed class WatchdogUiHost(KioskUiWatchdog watchdog) : IUiHost
{
    public Task StopAsync(CancellationToken cancellationToken) => watchdog.HoldAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        watchdog.Release();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A provisioned Linux kiosk: systemd runs the UI (<c>neoprint-kiosk.service</c>,
/// under cage), and the agent's sudoers rule allows exactly these two commands.
/// </summary>
public sealed class SystemdUiHost(ILogger<SystemdUiHost> logger) : IUiHost
{
    public Task StopAsync(CancellationToken cancellationToken) => RunAsync("stop", cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => RunAsync("start", cancellationToken);

    private async Task RunAsync(string verb, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("sudo", ["-n", "/usr/bin/systemctl", verb, "neoprint-kiosk.service"])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("sudo could not be started.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            logger.LogError("systemctl {Verb} neoprint-kiosk ended with {Code}: {Error}", verb, process.ExitCode, error.Trim());
            throw new InvalidOperationException($"systemctl {verb} neoprint-kiosk failed.");
        }
    }
}

/// <summary>
/// Windows: Shell Launcher owns the UI and starts it again when it exits; the
/// agent ends it and Shell Launcher brings up whatever <c>current</c> points at.
/// Not checked on a real Windows kiosk yet (T-093).
/// </summary>
public sealed class ShellLauncherUiHost(ILogger<ShellLauncherUiHost> logger) : IUiHost
{
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("NeoPrint.Kiosk"))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning(exception, "The kiosk UI could not be ended.");
                }
            }
        }
    }

    // Shell Launcher may already have started the old version again while the
    // junction was moved: ended once more, it comes back from the new one.
    public Task StartAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);
}
