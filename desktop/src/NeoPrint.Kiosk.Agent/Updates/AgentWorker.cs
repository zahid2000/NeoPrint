using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// The agent's update loop (T-321): at start, whatever a power cut left half
/// done is finished or undone; then the UI's requests are taken one at a time.
/// </summary>
public sealed class AgentWorker(UpdateApplier applier, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await applier.StartAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The agent could not finish what was left of an update.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await applier.ProcessPendingAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(applier.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The agent's update loop failed; trying again.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Runs <c>provision.sh --repair</c> of a version folder (Linux: the sudoers
/// rule allows exactly that); <c>Agent:ProvisioningCommand</c> replaces the
/// command where a real run cannot be made (the container kiosk).
/// </summary>
public sealed class ProvisioningRunner(IOptions<AgentOptions> options, ILogger<ProvisioningRunner> logger) : IProvisioningRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(20);

    public async Task<int> RunAsync(string scriptsFolder, CancellationToken cancellationToken)
    {
        var template = string.IsNullOrWhiteSpace(options.Value.ProvisioningCommand)
            ? "sudo -n {folder}/provision.sh --repair"
            : options.Value.ProvisioningCommand;
        var parts = template.Replace("{folder}", scriptsFolder, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);

        using var process = Process.Start(new ProcessStartInfo(parts[0], parts[1..])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = scriptsFolder,
        }) ?? throw new InvalidOperationException("The provisioning scripts could not be started.");

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            logger.LogError("The provisioning scripts ran longer than {Minutes} minutes and were stopped.", Limit.TotalMinutes);
            return -1;
        }

        // The scripts keep their own log (provision.log); only the tail is noted here.
        var tail = string.Join(' ', ((await output.ConfigureAwait(false)) + (await errors.ConfigureAwait(false))).Split('\n').TakeLast(3));
        logger.LogInformation("Provisioning ended with {Code}: {Tail}", process.ExitCode, tail.Trim());
        return process.ExitCode;
    }
}
