namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A test of a Linux-only control (prlimit): skipped on a Windows development
/// machine, run by the backend CI job on ubuntu-latest.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only: the resource limits are applied with prlimit.";
        }
    }
}
