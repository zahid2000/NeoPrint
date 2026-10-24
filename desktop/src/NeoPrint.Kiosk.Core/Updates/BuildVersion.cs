using System.Reflection;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The version a build was released as (<c>-p:Version=0.8.0-pilot.1</c> in the
/// release workflow), with the pre-release part a package version has and
/// <see cref="AssemblyName.Version"/> drops - the agent and the health check
/// compare it with the manifest's (T-321).
/// </summary>
public static class BuildVersion
{
    public static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // "0.8.0-pilot.1+3f2a…": the build metadata is not part of the version.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
