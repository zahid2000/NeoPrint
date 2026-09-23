namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips instead of failing when there is no
/// Docker daemon to talk to.
/// </summary>
/// <remarks>
/// CI always has Docker, so these tests really do run there (plan section 11).
/// Locally, a developer working on, say, the pricing rules should not be
/// blocked by a container runtime they do not have started.
/// </remarks>
public sealed class DockerFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> DockerAvailable = new(Probe, isThreadSafe: true);

    public DockerFactAttribute()
    {
        if (!DockerAvailable.Value)
        {
            Skip = "Docker is not available on this machine; container-backed tests are skipped.";
        }
    }

    /// <summary>
    /// Looks for the daemon endpoint rather than talking to it: this runs at
    /// test discovery, where a blocking call would stall the whole run.
    /// </summary>
    private static bool Probe()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            {
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                // Named pipes are enumerable through the pipe filesystem;
                // File.Exists does not work for them.
                return Directory
                    .EnumerateFiles(@"\\.\pipe\")
                    .Any(pipe => pipe.EndsWith("docker_engine", StringComparison.OrdinalIgnoreCase));
            }

            return File.Exists("/var/run/docker.sock");
        }
        catch
        {
            return false;
        }
    }
}
