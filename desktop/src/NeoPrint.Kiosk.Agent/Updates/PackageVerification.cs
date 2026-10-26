using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// The agent binary as a checker (T-327): a provisioning script on a new
/// kiosk downloads the newest release from the backend, which is not trusted
/// for it, and asks the agent on its setup stick - built with the release keys
/// compiled in - whether the package is one the NeoPrint team signed. Nothing
/// is installed here; the script installs what passed.
/// </summary>
public static class PackageVerification
{
    /// <summary>
    /// <c>verify-package &lt;package&gt; &lt;manifest&gt; &lt;signature&gt; &lt;component&gt;</c>
    /// for this machine's platform, with the key set compiled into this build.
    /// </summary>
    /// <returns>0 the package is good (its version on standard output), 2 refused, 64 not understood.</returns>
    public static async Task<int> RunAsync(string[] args, string agentVersion, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length != 5 || !TryComponent(args[4], out var component))
        {
            await error.WriteLineAsync("Usage: verify-package <package> <manifest> <signature> <app|agent|provisioning>").ConfigureAwait(false);
            return 64;
        }

        return await RunAsync(new ReleasePackageCheck(), args[1], args[2], args[3], component, UpdatePaths.Platform, agentVersion, output, error, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The same with a given key set and platform: for tests.</summary>
    public static async Task<int> RunAsync(
        ReleasePackageCheck check,
        string package,
        string manifest,
        string signature,
        ReleaseComponent component,
        string platform,
        string agentVersion,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!File.Exists(package) || !File.Exists(manifest) || !File.Exists(signature))
        {
            await error.WriteLineAsync("Refused: a file is missing - the package, its manifest and its signature are all needed.").ConfigureAwait(false);
            return 2;
        }

        // Nothing installed to compare with, and no withdrawn list: the server
        // already left out what was withdrawn, and the script installs afresh.
        // The agent's own version still bounds what a package may require of it.
        var result = await check.CheckAsync(
                manifest,
                signature,
                package,
                new ReleaseExpectation(
                    component,
                    platform,
                    string.Empty,
                    null,
                    ReleaseVersion.TryParse(agentVersion, out var agent) ? agent : ReleaseVersion.Parse("0.0.0"),
                    []),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Accepted || result.Manifest is not { } accepted)
        {
            await error.WriteLineAsync($"Refused: {result.Rejection} - {result.Detail}").ConfigureAwait(false);
            return 2;
        }

        await output.WriteLineAsync(accepted.Version.ToString()).ConfigureAwait(false);
        return 0;
    }

    private static bool TryComponent(string value, out ReleaseComponent component)
    {
        component = value switch
        {
            "app" => ReleaseComponent.App,
            "agent" => ReleaseComponent.Agent,
            "provisioning" => ReleaseComponent.Provisioning,
            _ => (ReleaseComponent)(-1),
        };

        return Enum.IsDefined(component);
    }
}
