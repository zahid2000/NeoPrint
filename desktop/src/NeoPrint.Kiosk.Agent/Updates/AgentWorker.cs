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
/// Asks the root-owned helper to install or run the provisioning scripts
/// (<see cref="RootProvisioning"/>): on a provisioned Linux kiosk through its
/// one sudo rule, <c>sudo -n /usr/local/lib/neoprint/neoprint-reprovision</c>.
/// With <c>Agent:ProvisioningHelper</c> empty the agent is the helper itself -
/// the container kiosk, where it runs as root and the scripts only dry-run.
/// </summary>
public sealed class ProvisioningRunner(
    IOptions<AgentOptions> options, NeoPrint.Kiosk.Core.Updates.UpdatePaths paths, ILogger<ProvisioningRunner> logger) : IProvisioningRunner
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(25);

    public Task<int> InstallAsync(string package, string manifestPath, string signaturePath, CancellationToken cancellationToken)
        => RunAsync(["install", package, manifestPath, signaturePath], cancellationToken);

    public Task<int> ReapplyAsync(CancellationToken cancellationToken) => RunAsync(["reapply"], cancellationToken);

    private async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var scriptArgs = options.Value.ProvisioningScriptArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(options.Value.ProvisioningHelper))
        {
            var code = await RootProvisioning.RunAsync(["provisioning", .. arguments, .. scriptArgs], paths,
                NeoPrint.Kiosk.Core.Updates.BuildVersion.Of(typeof(ProvisioningRunner).Assembly), cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                logger.LogError("The provisioning scripts ended with {Code}; what they said is on the agent's standard error.", code);
            }

            return code;
        }

        var helper = options.Value.ProvisioningHelper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limit);

        using var process = Process.Start(new ProcessStartInfo(helper[0], [.. helper[1..], .. arguments])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The provisioning helper could not be started.");

        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            logger.LogError("The provisioning helper ran longer than {Minutes} minutes and was stopped.", Limit.TotalMinutes);
            return 3;
        }

        if (process.ExitCode != 0)
        {
            logger.LogError("The provisioning helper ended with {Code}: {Errors}", process.ExitCode, (await errors.ConfigureAwait(false)).Trim());
        }

        return process.ExitCode;
    }
}
