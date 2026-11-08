using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

/// <summary>
/// "Kiosk şəkli" (T-340): the picture the app shows on a kiosk's cards.
/// </summary>
/// <remarks>
/// The panel re-encodes the picture in the browser before sending it (no
/// image library in the API process, ADR-0005); the API still trusts only
/// the bytes: a JPEG, PNG or WebP signature and a size limit, the type taken
/// from the bytes, never from the request. It is stored encrypted like every
/// other object and served with <c>nosniff</c>.
/// </remarks>
/// <param name="Content">The picture's bytes.</param>
public sealed record SetKioskPhotoCommand(Guid KioskId, byte[] Content, Guid AdminUserId, string? Ip)
    : IRequest<KioskPhotoInfo>;

/// <summary>Takes the picture away; the app shows its shop icon again.</summary>
public sealed record RemoveKioskPhotoCommand(Guid KioskId, Guid AdminUserId, string? Ip) : IRequest;

/// <summary>The picture itself, for the app (any kiosk a customer can find) and the panel.</summary>
public sealed record GetKioskPhotoQuery(Guid KioskId, bool ListedOnly) : IRequest<KioskPhotoContent?>;

/// <param name="Version">Changes with every new picture; part of <see cref="KioskPhotoUrls"/>.</param>
public sealed record KioskPhotoInfo(long Version, string ContentType, DateTime UpdatedAt);

public sealed record KioskPhotoContent(byte[] Bytes, string ContentType, long Version);

/// <summary>Where the app fetches a kiosk's picture: relative to the API, versioned for caching.</summary>
public static class KioskPhotoUrls
{
    public static string? For(Kiosk kiosk)
        => kiosk.Photo is { } photo ? $"api/kiosks/{kiosk.Id}/photo?v={photo.Version}" : null;
}

public sealed class KioskPhotoHandlers :
    IRequestHandler<SetKioskPhotoCommand, KioskPhotoInfo>,
    IRequestHandler<RemoveKioskPhotoCommand>,
    IRequestHandler<GetKioskPhotoQuery, KioskPhotoContent?>
{
    /// <summary>A picture re-encoded for a card is well under this.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;
    private readonly IFileStorage _storage;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<KioskPhotoHandlers> _logger;

    public KioskPhotoHandlers(
        IKioskRepository kiosks,
        IPermissionResolver permissions,
        IFileStorage storage,
        IAuditWriter audit,
        IClock clock,
        ILogger<KioskPhotoHandlers> logger)
    {
        _kiosks = kiosks;
        _permissions = permissions;
        _storage = storage;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>What the bytes are, by their signature; null when not a picture we take.</summary>
    public static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 12
            && bytes[..4].SequenceEqual("RIFF"u8)
            && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    public async Task<KioskPhotoInfo> Handle(SetKioskPhotoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content.Length == 0)
        {
            throw Invalid("kiosks.photoEmpty");
        }

        if (request.Content.Length > MaxBytes)
        {
            throw new ValidationException(
                [new ValidationError("photo", "kiosks.photoTooLarge", ("maxMb", MaxBytes / (1024 * 1024)))]);
        }

        var type = ImageType(request.Content) ?? throw Invalid("kiosks.photoNotImage");
        var kiosk = await LoadAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        // A new key for every picture: an app never gets the old bytes under
        // the new version.
        StoredObject stored;
        using (var content = new MemoryStream(request.Content, writable: false))
        {
            stored = await _storage
                .WriteAsync(StorageBucket.Files, $"kiosks/{kiosk.Id:N}/photo-{now.Ticks}", content, type, cancellationToken)
                .ConfigureAwait(false);
        }

        var before = Snapshot(kiosk.Photo);
        var previous = kiosk.ReplacePhoto(
            new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
            type,
            now);

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "kiosks.photo", kiosk, before, Snapshot(kiosk.Photo), request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return new KioskPhotoInfo(kiosk.Photo!.Version, kiosk.Photo.ContentType, kiosk.Photo.UpdatedAt);
    }

    public async Task Handle(RemoveKioskPhotoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await LoadAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);

        // Already gone: the same end, nothing to write.
        if (kiosk.Photo is null)
        {
            return;
        }

        var before = Snapshot(kiosk.Photo);
        var previous = kiosk.RemovePhoto();

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "kiosks.photo-removed", kiosk, before, null, request.Ip, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<KioskPhotoContent?> Handle(GetKioskPhotoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        // A customer finds only the kiosks they can go to (KioskSearch).
        if (kiosk?.Photo is not { } photo
            || (request.ListedOnly
                && kiosk.Status is not (KioskStatus.Online or KioskStatus.Paused or KioskStatus.Offline or KioskStatus.Maintenance)))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await _storage.ReadAsync(ToStored(photo.Object), StorageBucket.Files, buffer, cancellationToken).ConfigureAwait(false);

        return new KioskPhotoContent(buffer.ToArray(), photo.ContentType, photo.Version);
    }

    private async Task<Kiosk> LoadAsync(Guid kioskId, Guid adminUserId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        return kiosk;
    }

    /// <summary>
    /// The replaced picture's object goes once the kiosk points at the new
    /// one. A delete that fails leaves an unreferenced encrypted object -
    /// never a kiosk pointing at nothing - so it does not fail the change.
    /// </summary>
    private async Task DeleteQuietlyAsync(KioskPhoto? previous, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            await _storage.DeleteAsync(ToStored(previous.Object), StorageBucket.Files, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The change itself stands; the object is left for a clean-up.
            _logger.LogWarning(
                exception,
                "A replaced kiosk photo could not be deleted ({StorageKey})",
                previous.Object.Key);
        }
    }

    private static StoredObject ToStored(FileObject stored) => new(
        stored.ProviderId,
        stored.Key,
        stored.Sha256,
        stored.EncryptedLength,
        stored.PlaintextLength,
        stored.KekId);

    private static string? Snapshot(KioskPhoto? photo) => photo is null
        ? null
        : JsonSerializer.Serialize(new { photo.ContentType, photo.Version, Bytes = photo.Object.PlaintextLength });

    private Task AuditAsync(
        Guid adminUserId,
        string action,
        Kiosk kiosk,
        string? before,
        string? after,
        string? ip,
        CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminUserId, action, "Kiosk", kiosk.Id.ToString(), before, after, ip, Reason: null),
            cancellationToken);

    private static ValidationException Invalid(string key) => new([new ValidationError("photo", key)]);
}
