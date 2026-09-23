using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Pins the Docker API version the container library negotiates to whatever the
/// local engine actually supports.
/// </summary>
/// <remarks>
/// Testcontainers asks for a recent API version, and an engine older than that
/// answers <c>client version X is too new</c> rather than negotiating down. CI
/// runs a current engine, but a developer machine may well be a release or two
/// behind, and that should not look like "Docker is not running". Asking the
/// Docker CLI what the server speaks and passing it on through
/// <c>DOCKER_API_VERSION</c> makes the container tests work on both.
/// </remarks>
internal static class DockerApiVersionInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_API_VERSION")))
        {
            return;
        }

        var version = QueryServerApiVersion();
        if (!string.IsNullOrWhiteSpace(version))
        {
            Environment.SetEnvironmentVariable("DOCKER_API_VERSION", version);
        }
    }

    private static string? QueryServerApiVersion()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.APIVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();

            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            // No Docker CLI on the machine. DockerFactAttribute will skip the
            // container-backed tests anyway.
            return null;
        }
    }
}
