using System.Diagnostics;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// The provisioning scripts run as root, so the agent - the unprivileged
/// <c>neoprint</c> account - must not be able to choose what runs (T-321).
/// This is the agent binary's root mode, run from the root-owned copy that
/// provisioning keeps in <c>/usr/local/lib/neoprint/agent</c>
/// (<c>neoprint-reprovision</c>, the one sudo rule for it): it checks the
/// package itself with the keys compiled into that copy, unpacks it into the
/// root-owned <c>provisioning/versions</c>, switches <c>current</c> and runs the
/// scripts - and puts the previous version back if they fail.
/// </summary>
public static class RootProvisioning
{
    /// <summary><c>provisioning install &lt;package&gt; &lt;manifest&gt; &lt;signature&gt; [script args]</c> or <c>provisioning reapply [script args]</c>.</summary>
    /// <returns>The process exit code: 0 done, 2 refused, 3 failed, 64 not understood.</returns>
    public static async Task<int> RunAsync(string[] args, UpdatePaths paths, string agentVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Whoever calls this through sudo chooses nothing the scripts do: repair
        // mode, or a dry run - never --offline-package or anything else.
        var scriptArgs = args.Where(arg => arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (scriptArgs.Any(arg => arg is not ("--repair" or "--dry-run")))
        {
            await Console.Error.WriteLineAsync("Only --repair or --dry-run may be passed to the provisioning scripts.").ConfigureAwait(false);
            return 64;
        }

        args = [.. args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))];
        var script = scriptArgs.Length == 0 ? "--repair" : string.Join(' ', scriptArgs);

        if (args.Length >= 2 && args[1] == "reapply")
        {
            return await ReapplyAsync(paths, script, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 5 && args[1] == "install")
        {
            return await InstallAsync(paths, args[2], args[3], args[4], agentVersion, script, cancellationToken).ConfigureAwait(false);
        }

        await Console.Error.WriteLineAsync("Usage: provisioning install <package> <manifest> <signature> [--dry-run] | provisioning reapply [--dry-run]").ConfigureAwait(false);
        return 64;
    }

    public static async Task<int> InstallAsync(
        UpdatePaths paths, string package, string manifestPath, string signaturePath, string agentVersion, string scriptArgs, CancellationToken cancellationToken)
    {
        var links = new VersionLinks(paths.Versions(UpdateComponents.Provisioning), paths.Current(UpdateComponents.Provisioning));
        var installed = links.CurrentVersion();

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var check = await new ReleasePackageCheck().CheckAsync(
                manifestBytes,
                await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false),
                package,
                new ReleaseExpectation(
                    ReleaseComponent.Provisioning,
                    UpdatePaths.Platform,
                    string.Empty,
                    installed is not null && ReleaseVersion.TryParse(installed, out var running) ? running : null,
                    ReleaseVersion.TryParse(agentVersion, out var agent) ? agent : ReleaseVersion.Parse("0.0.0"),
                    []),
                cancellationToken)
            .ConfigureAwait(false);

        if (!check.Accepted || check.Manifest is not { } manifest)
        {
            await Console.Error.WriteLineAsync($"Refused: {check.Rejection} - {check.Detail}").ConfigureAwait(false);
            return 2;
        }

        var version = manifest.Version.ToString();
        var target = links.VersionFolder(version);
        var partial = target + ".partial";
        try
        {
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            await PackageArchive.ExtractAsync(package, partial, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(partial, VersionLinks.CompleteMarker), version, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(partial, target);
        }
        catch (InvalidDataException exception)
        {
            await Console.Error.WriteLineAsync($"The package could not be unpacked: {exception.Message}").ConfigureAwait(false);
            return 3;
        }

        links.SwitchTo(version);
        if (await RunScriptsAsync(target, scriptArgs, cancellationToken).ConfigureAwait(false) == 0)
        {
            links.Prune(3, version, installed);
            return 0;
        }

        if (installed is not null)
        {
            links.SwitchTo(installed);
        }

        return 3;
    }

    public static async Task<int> ReapplyAsync(UpdatePaths paths, string scriptArgs, CancellationToken cancellationToken)
    {
        var links = new VersionLinks(paths.Versions(UpdateComponents.Provisioning), paths.Current(UpdateComponents.Provisioning));
        return links.CurrentVersion() is { } current
            ? await RunScriptsAsync(links.VersionFolder(current), scriptArgs, cancellationToken).ConfigureAwait(false) == 0 ? 0 : 3
            : 3;
    }

    private static async Task<int> RunScriptsAsync(string folder, string scriptArgs, CancellationToken cancellationToken)
    {
        var script = Path.Combine(folder, OperatingSystem.IsWindows() ? "provision.ps1" : "provision.sh");
        if (!File.Exists(script))
        {
            await Console.Error.WriteLineAsync($"{script} is not there.").ConfigureAwait(false);
            return 3;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));

        using var process = Process.Start(new ProcessStartInfo("/bin/bash", [script, .. scriptArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)])
        {
            UseShellExecute = false,
            WorkingDirectory = folder,
        }) ?? throw new InvalidOperationException("The provisioning scripts could not be started.");

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return 3;
        }
    }
}
