using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Crypto;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// The camera archive (T-485): besides its own disk, a kiosk sends each sealed
// minute to the backend, which keeps it on the file server (a storage
// provider) the panel chose - for every kiosk, or another for one kiosk -
// for Camera.ArchiveRetentionDays. The kiosk holds no credential for it.
// ---------------------------------------------------------------------------

/// <summary>Writes camera minutes to a storage provider as they are: they are already sealed.</summary>
public interface ICameraArchiveStore
{
    /// <summary>Whether a storage provider by that id is configured.</summary>
    Task<bool> HasProviderAsync(Guid providerId, CancellationToken cancellationToken = default);

    /// <returns>The SHA-256 (hex) and length of what was stored.</returns>
    Task<(string Sha256, long Length)> WriteAsync(Guid providerId, string key, Stream content, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(Guid providerId, string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes it; one already gone is not an error.</summary>
    Task DeleteAsync(Guid providerId, string key, CancellationToken cancellationToken = default);
}

public interface ICameraSegmentRepository
{
    /// <summary>False when the kiosk already sent that minute.</summary>
    Task<bool> TryAddAsync(CameraSegment segment, CancellationToken cancellationToken = default);

    Task<CameraSegment?> FindAsync(Guid kioskId, DateTime start, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CameraSegment>> ListExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Where a kiosk's camera minutes go (T-485).</summary>
public static class CameraArchiveTarget
{
    /// <summary>A kiosk's own choice: none of its minutes go to a file server.</summary>
    public const string Off = "off";

    /// <summary>Null (the platform setting), <see cref="Off"/>, or a storage provider's id.</summary>
    public static bool IsValid(string? value)
        => value is null || value == Off || Guid.TryParse(value, out _);

    /// <summary>The provider a kiosk's minutes go to, or null when they stay on the kiosk.</summary>
    /// <param name="common"><c>Camera.ArchiveProvider</c>: what a kiosk without its own choice follows.</param>
    public static async Task<Guid?> ResolveAsync(
        Kiosk kiosk, string? common, ICameraArchiveStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(store);

        var chosen = kiosk.CameraArchive ?? common;

        // A provider removed since it was chosen: nowhere to write, the kiosk keeps its own.
        return Guid.TryParse(chosen, out var providerId)
            && await store.HasProviderAsync(providerId, cancellationToken).ConfigureAwait(false)
                ? providerId
                : null;
    }
}

/// <summary>One sealed minute from the kiosk (<c>POST kiosks/me/camera/segments</c>).</summary>
/// <param name="Start">When the minute starts, UTC, as its file name says.</param>
public sealed record UploadCameraSegmentCommand(Guid DeviceId, Stream Body, DateTime Start, int Seconds) : IRequest<CameraSegmentView>;

/// <summary>Deletes the archived minutes past <c>Camera.ArchiveRetentionDays</c>.</summary>
public sealed record PurgeExpiredCameraSegmentsCommand : IRequest<int>;

/// <param name="AlreadyStored">The kiosk had sent it before: nothing was written again.</param>
public sealed record CameraSegmentView(Guid Id, DateTime Start, DateTime End, long Bytes, DateTime ExpiresAt, bool AlreadyStored);

public sealed class CameraArchiveHandlers :
    IRequestHandler<UploadCameraSegmentCommand, CameraSegmentView>,
    IRequestHandler<PurgeExpiredCameraSegmentsCommand, int>
{
    /// <summary>A 1080p minute at the highest quality is under 30 MB.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public const int MaxSeconds = 120;

    private const int PurgeBatch = 500;

    private readonly ICameraSegmentRepository _segments;
    private readonly ICameraArchiveStore _store;
    private readonly ICameraKeys _keys;
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<CameraArchiveHandlers> _logger;

    public CameraArchiveHandlers(
        ICameraSegmentRepository segments,
        ICameraArchiveStore store,
        ICameraKeys keys,
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ISettingsReader settings,
        IClock clock,
        ILogger<CameraArchiveHandlers> logger)
    {
        _segments = segments;
        _store = store;
        _keys = keys;
        _devices = devices;
        _kiosks = kiosks;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="UploadCameraSegmentCommand" />
    public async Task<CameraSegmentView> Handle(UploadCameraSegmentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("devices.notBound");

        var now = _clock.UtcNow;
        var start = DateTime.SpecifyKind(request.Start, DateTimeKind.Utc);

        // A kiosk is an untrusted device: a minute from before the longest a
        // kiosk keeps one, or from the future, is not taken.
        if (request.Seconds is < 1 or > MaxSeconds
            || start < now.AddDays(-(CameraSettings.MaxRetentionDays + 1))
            || start > now.AddMinutes(10))
        {
            throw new DomainException("camera.segmentInvalid");
        }

        var existing = await _segments.FindAsync(kioskId, start, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return View(existing, alreadyStored: true);
        }

        var common = await _settings.GetAsync(SettingKeys.CameraArchiveProvider, cancellationToken).ConfigureAwait(false);
        var providerId = await CameraArchiveTarget.ResolveAsync(kiosk, common, _store, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("camera.archiveOff");

        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-camera-{Guid.NewGuid():N}");
        await using var buffer = new FileStream(
            scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        await CopyLimitedAsync(request.Body, buffer, cancellationToken).ConfigureAwait(false);

        // Only what this backend can open is kept: the file names a camera key it holds.
        buffer.Position = 0;
        SealedSegmentHeader header;
        try
        {
            header = await CameraSegmentCipher.ReadHeaderAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptoFormatException)
        {
            throw new DomainException("camera.segmentInvalid");
        }

        using (var key = await _keys.OpenPrivateKeyAsync(header.KeyId, cancellationToken).ConfigureAwait(false))
        {
            if (key is null)
            {
                throw new DomainException("camera.segmentInvalid");
            }
        }

        buffer.Position = 0;
        var objectKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"camera/{kioskId:N}/{start:yyyy}/{start:MM}/{start:dd}/{start:yyyyMMdd'T'HHmmss'Z'}-{request.Seconds}.npcam");
        var (sha, length) = await _store.WriteAsync(providerId, objectKey, buffer, cancellationToken).ConfigureAwait(false);

        var retention = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.CameraArchiveRetentionDays, 30, cancellationToken).ConfigureAwait(false),
            CameraSettings.MinArchiveRetentionDays,
            CameraSettings.MaxArchiveRetentionDays);

        var segment = new CameraSegment
        {
            KioskId = kioskId,
            Start = start,
            End = start.AddSeconds(request.Seconds),
            Bytes = length,
            ProviderId = providerId,
            Key = objectKey,
            Sha256 = sha,
            KeyId = header.KeyId,
            CreatedAt = now,
            ExpiresAt = start.AddDays(retention),
        };

        if (!await _segments.TryAddAsync(segment, cancellationToken).ConfigureAwait(false))
        {
            // Sent twice at the same moment: the other request recorded it, under the same key.
            var other = await _segments.FindAsync(kioskId, start, cancellationToken).ConfigureAwait(false);
            return View(other ?? segment, alreadyStored: true);
        }

        return View(segment, alreadyStored: false);
    }

    /// <inheritdoc cref="PurgeExpiredCameraSegmentsCommand" />
    public async Task<int> Handle(PurgeExpiredCameraSegmentsCommand request, CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var expired = await _segments.ListExpiredAsync(_clock.UtcNow, PurgeBatch, cancellationToken).ConfigureAwait(false);
            var failed = 0;

            foreach (var segment in expired)
            {
                try
                {
                    await _store.DeleteAsync(segment.ProviderId, segment.Key, cancellationToken).ConfigureAwait(false);
                    await _segments.DeleteAsync(segment.Id, cancellationToken).ConfigureAwait(false);
                    deleted++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // An unreachable server: the next run tries again; the record keeps it findable.
                    failed++;
                    _logger.LogWarning(exception, "An archived camera minute could not be deleted from provider {ProviderId}.", segment.ProviderId);
                }
            }

            // A full batch that could not be deleted would come back the same: stop for this run.
            if (expired.Count < PurgeBatch || failed == expired.Count)
            {
                break;
            }
        }

        return deleted;
    }

    private static async Task CopyLimitedAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new DomainException("camera.segmentTooLarge");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total == 0)
        {
            throw new DomainException("camera.segmentInvalid");
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CameraSegmentView View(CameraSegment segment, bool alreadyStored)
        => new(segment.Id, segment.Start, segment.End, segment.Bytes, segment.ExpiresAt, alreadyStored);
}
