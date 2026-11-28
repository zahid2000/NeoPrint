using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Agent;

/// <summary>
/// Installs the camera's FFmpeg on a Windows kiosk set up without it (T-496):
/// the kiosk application asks (<see cref="FfmpegSetup.WantedFile"/>), the
/// agent - running as LocalSystem, so it may write to Program Files -
/// downloads the pinned build, checks it and unpacks it. Nobody has to go to
/// the kiosk. On Linux provisioning installs it with apt.
/// </summary>
/// <remarks>
/// The one thing the agent fetches from outside the backend: a public file
/// whose address, size and SHA-256 are compiled into this signed build.
/// </remarks>
public sealed class FfmpegInstallerService : BackgroundService
{
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(1);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly FfmpegInstaller _installer;
    private readonly ILogger<FfmpegInstallerService> _logger;

    public FfmpegInstallerService(UpdatePaths paths, IClock clock, ILogger<FfmpegInstallerService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // ProgramData\NeoPrint\camera: the kiosk application's camera directory.
        var data = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths.UpdatesRoot)) ?? paths.UpdatesRoot;
        _installer = new FfmpegInstaller(paths.InstallRoot, Path.Combine(data, "camera"), FfmpegPin.Windows, DownloadAsync, clock, logger);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _installer.StepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "The FFmpeg installer stumbled; it looks again shortly.");
            }

            try
            {
                await Task.Delay(Poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task DownloadAsync(Uri address, Stream target, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await body.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }
}
