using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// Fetches an offered package into the downloads folder and keeps what has
/// arrived: a download cut off by a power cut or a dropped line goes on from
/// where it stopped (docs/KIOSK_UPDATES.md, step 2; T-321).
/// </summary>
public sealed class ReleaseDownloader
{
    private const int Attempts = 3;

    private readonly KioskApiClient _api;
    private readonly UpdatePaths _paths;
    private readonly int _kilobytesPerSecond;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _logger;

    /// <param name="kilobytesPerSecond">A limit, so an update does not take the kiosk's whole line (0: none).</param>
    public ReleaseDownloader(KioskApiClient api, UpdatePaths paths, int kilobytesPerSecond, ILogger logger, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _paths = paths;
        _kilobytesPerSecond = kilobytesPerSecond;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public string PackagePath(Guid releaseId) => Path.Combine(_paths.Downloads, $"{releaseId:N}.pkg");

    public string PartPath(Guid releaseId) => Path.Combine(_paths.Downloads, $"{releaseId:N}.part");

    /// <returns>The complete package's path.</returns>
    /// <exception cref="ReleaseDownloadException">It could not be fetched; what arrived is kept for the next attempt.</exception>
    public async Task<string> FetchAsync(KioskReleaseOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        Directory.CreateDirectory(_paths.Downloads);
        var package = PackagePath(offer.ReleaseId);
        if (File.Exists(package) && new FileInfo(package).Length == offer.Size)
        {
            return package;
        }

        var part = PartPath(offer.ReleaseId);
        for (var attempt = 1; ; attempt++)
        {
            var from = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (from >= offer.Size)
            {
                // More than the package: not this package. Started over.
                File.Delete(part);
                from = 0;
            }

            if (from > 0)
            {
                _logger.LogInformation("Resuming the download of {Component} {Version} at {From} of {Size} bytes.", offer.Component, offer.Version, from, offer.Size);
            }

            try
            {
                await _api.DownloadReleaseAsync(
                        offer.Download,
                        from,
                        resumed => new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true),
                        _kilobytesPerSecond > 0 ? ThrottleAsync : null,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            catch (ReleaseDownloadException exception) when (attempt < Attempts && !exception.Message.Contains("not offered", StringComparison.Ordinal))
            {
                _logger.LogWarning(exception, "Download of {Component} {Version} broke off (attempt {Attempt}); trying again.", offer.Component, offer.Version, attempt);
                await _delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        var length = new FileInfo(part).Length;
        if (length != offer.Size)
        {
            throw new ReleaseDownloadException($"The download ended at {length} of {offer.Size} bytes.");
        }

        File.Move(part, package, overwrite: true);
        return package;
    }

    /// <summary>Removes what is downloaded for releases that are not offered any more.</summary>
    public void Forget(IEnumerable<Guid> keep)
    {
        if (!Directory.Exists(_paths.Downloads))
        {
            return;
        }

        var kept = keep.Select(id => id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(_paths.Downloads))
        {
            if (!kept.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                File.Delete(file);
            }
        }
    }

    private Task ThrottleAsync(long bytes, CancellationToken cancellationToken)
        => _delay(TimeSpan.FromSeconds(bytes / (_kilobytesPerSecond * 1024d)), cancellationToken);
}
