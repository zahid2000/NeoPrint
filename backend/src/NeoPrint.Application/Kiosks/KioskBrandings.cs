using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

/// <summary>The default's document and each kiosk's own (<see cref="KioskBranding"/>).</summary>
public interface IKioskBrandingRepository
{
    /// <summary><see cref="KioskBranding.DefaultId"/> for the default, a kiosk's id for its own.</summary>
    Task<KioskBranding?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(KioskBranding branding, CancellationToken cancellationToken = default);
}

/// <summary>The default (<paramref name="KioskId"/> null) or one kiosk's words and picture, for the panel.</summary>
public sealed record GetKioskBrandingQuery(Guid? KioskId, Guid AdminUserId) : IRequest<KioskBrandingView>;

/// <summary>Puts these texts in place; an empty one falls back (to the default, then to the kiosk's own).</summary>
public sealed record SetKioskBrandingTextsCommand(
    Guid? KioskId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Texts,
    Guid AdminUserId,
    string? Ip) : IRequest<KioskBrandingView>;

/// <summary>The picture beside the big line, re-encoded by the panel; the bytes are still checked.</summary>
public sealed record SetKioskBrandingImageCommand(Guid? KioskId, byte[] Content, Guid AdminUserId, string? Ip)
    : IRequest<KioskBrandingView>;

public sealed record RemoveKioskBrandingImageCommand(Guid? KioskId, Guid AdminUserId, string? Ip) : IRequest<KioskBrandingView>;

/// <summary>The picture of the default or of one kiosk, as stored - for the panel's preview.</summary>
public sealed record GetKioskBrandingImageQuery(Guid? KioskId, Guid AdminUserId) : IRequest<KioskPhotoContent?>;

/// <summary>The picture this device's kiosk shows: its own, else the default's.</summary>
public sealed record GetDeviceBrandingImageQuery(Guid DeviceId) : IRequest<KioskPhotoContent?>;

/// <param name="Texts">What is set here: key → language → text.</param>
/// <param name="Defaults">For a kiosk, what the default says - the panel shows it where the kiosk has nothing of its own.</param>
/// <param name="Image">The picture set here, if any.</param>
/// <param name="DefaultImage">For a kiosk, the default's picture, if any.</param>
public sealed record KioskBrandingView(
    Guid? KioskId,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Defaults,
    KioskPhotoInfo? Image,
    KioskPhotoInfo? DefaultImage,
    IReadOnlyList<string> Keys,
    int MaxLength,
    DateTime? UpdatedAt);

/// <summary>
/// What a kiosk's configuration carries (T-387): every text worked out per
/// language - its own, else the default's; a key absent means "the kiosk's
/// own words". The picture's version changes with every new picture.
/// </summary>
public sealed record KioskBrandingDto(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts,
    long? ImageVersion);

/// <summary>Works out a kiosk's words from its own document and the default.</summary>
public sealed class KioskBrandingResolver
{
    private readonly IKioskBrandingRepository _brandings;

    public KioskBrandingResolver(IKioskBrandingRepository brandings) => _brandings = brandings;

    public async Task<KioskBrandingDto> ResolveAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var own = await _brandings.FindAsync(kioskId, cancellationToken).ConfigureAwait(false);
        var fallback = await _brandings.FindAsync(KioskBranding.DefaultId, cancellationToken).ConfigureAwait(false);

        var texts = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var key in KioskBranding.Keys)
        {
            var languages = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var language in KioskBranding.Languages)
            {
                if ((own?.Text(key, language) ?? fallback?.Text(key, language)) is { } text)
                {
                    languages[language] = text;
                }
            }

            if (languages.Count > 0)
            {
                texts[key] = languages;
            }
        }

        return new KioskBrandingDto(texts, (own?.Image ?? fallback?.Image)?.Version);
    }

    /// <summary>The picture a kiosk shows: its own, else the default's.</summary>
    public async Task<KioskPhoto?> ImageAsync(Guid kioskId, CancellationToken cancellationToken)
        => (await _brandings.FindAsync(kioskId, cancellationToken).ConfigureAwait(false))?.Image
           ?? (await _brandings.FindAsync(KioskBranding.DefaultId, cancellationToken).ConfigureAwait(false))?.Image;
}

public sealed class KioskBrandingHandlers :
    IRequestHandler<GetKioskBrandingQuery, KioskBrandingView>,
    IRequestHandler<SetKioskBrandingTextsCommand, KioskBrandingView>,
    IRequestHandler<SetKioskBrandingImageCommand, KioskBrandingView>,
    IRequestHandler<RemoveKioskBrandingImageCommand, KioskBrandingView>,
    IRequestHandler<GetKioskBrandingImageQuery, KioskPhotoContent?>,
    IRequestHandler<GetDeviceBrandingImageQuery, KioskPhotoContent?>
{
    private readonly IKioskBrandingRepository _brandings;
    private readonly KioskBrandingResolver _resolver;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPermissionResolver _permissions;
    private readonly IFileStorage _storage;
    private readonly IKioskNotifier _notifier;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<KioskBrandingHandlers> _logger;

    public KioskBrandingHandlers(
        IKioskBrandingRepository brandings,
        KioskBrandingResolver resolver,
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IPermissionResolver permissions,
        IFileStorage storage,
        IKioskNotifier notifier,
        IAuditWriter audit,
        IClock clock,
        ILogger<KioskBrandingHandlers> logger)
    {
        _brandings = brandings;
        _resolver = resolver;
        _kiosks = kiosks;
        _devices = devices;
        _permissions = permissions;
        _storage = storage;
        _notifier = notifier;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task<KioskBrandingView> Handle(GetKioskBrandingQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = await ScopeAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        return await ViewAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KioskBrandingView> Handle(SetKioskBrandingTextsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = await ScopeAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var branding = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        var before = JsonSerializer.Serialize(branding.Texts);

        var wrong = branding.SetTexts(request.Texts, _clock.UtcNow);
        if (wrong.Count > 0)
        {
            throw new ValidationException(
                [.. wrong.Select(key => new ValidationError($"texts.{key}", "kiosks.brandingTextInvalid", ("max", KioskBranding.MaxLength)))]);
        }

        await _brandings.SaveAsync(branding, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "kiosks.branding", id, before, JsonSerializer.Serialize(branding.Texts), request.Ip, cancellationToken)
            .ConfigureAwait(false);
        await TellAsync(id, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KioskBrandingView> Handle(SetKioskBrandingImageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content.Length == 0)
        {
            throw Invalid("kiosks.photoEmpty");
        }

        if (request.Content.Length > KioskPhotoHandlers.MaxBytes)
        {
            throw new ValidationException(
                [new ValidationError("image", "kiosks.photoTooLarge", ("maxMb", KioskPhotoHandlers.MaxBytes / (1024 * 1024)))]);
        }

        // The type from the bytes, never from the request (as for the kiosk's own photo).
        var type = KioskPhotoHandlers.ImageType(request.Content) ?? throw Invalid("kiosks.photoNotImage");
        var id = await ScopeAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var branding = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        StoredObject stored;
        using (var content = new MemoryStream(request.Content, writable: false))
        {
            stored = await _storage
                .WriteAsync(StorageBucket.Files, $"kiosk-branding/{id:N}/image-{now.Ticks}", content, type, cancellationToken)
                .ConfigureAwait(false);
        }

        var before = Snapshot(branding.Image);
        var previous = branding.ReplaceImage(
            new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
            type,
            now);

        await _brandings.SaveAsync(branding, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "kiosks.branding-image", id, before, Snapshot(branding.Image), request.Ip, cancellationToken)
            .ConfigureAwait(false);
        await TellAsync(id, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KioskBrandingView> Handle(RemoveKioskBrandingImageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = await ScopeAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var branding = await _brandings.FindAsync(id, cancellationToken).ConfigureAwait(false);

        // Already gone: the same end, nothing to write.
        if (branding?.Image is null)
        {
            return await ViewAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var before = Snapshot(branding.Image);
        var previous = branding.RemoveImage(_clock.UtcNow);

        await _brandings.SaveAsync(branding, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "kiosks.branding-image-removed", id, before, null, request.Ip, cancellationToken)
            .ConfigureAwait(false);
        await TellAsync(id, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KioskPhotoContent?> Handle(GetKioskBrandingImageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = await ScopeAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var image = (await _brandings.FindAsync(id, cancellationToken).ConfigureAwait(false))?.Image;
        return image is null ? null : await ReadAsync(image, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KioskPhotoContent?> Handle(GetDeviceBrandingImageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device?.KioskId is not { } kioskId)
        {
            return null;
        }

        var image = await _resolver.ImageAsync(kioskId, cancellationToken).ConfigureAwait(false);
        return image is null ? null : await ReadAsync(image, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The document's id: the default's needs <c>settings.manage</c> (the
    /// controller checks it); a kiosk's must be one the caller sees.
    /// </summary>
    private async Task<Guid> ScopeAsync(Guid? kioskId, Guid adminUserId, CancellationToken cancellationToken)
    {
        if (kioskId is not { } id)
        {
            return KioskBranding.DefaultId;
        }

        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        return kiosk.Id;
    }

    private async Task<KioskBranding> LoadAsync(Guid id, CancellationToken cancellationToken)
        => await _brandings.FindAsync(id, cancellationToken).ConfigureAwait(false)
           ?? new KioskBranding { Id = id, CreatedAt = _clock.UtcNow };

    private async Task<KioskBrandingView> ViewAsync(Guid id, CancellationToken cancellationToken)
    {
        var own = await _brandings.FindAsync(id, cancellationToken).ConfigureAwait(false);
        var fallback = id == KioskBranding.DefaultId
            ? null
            : await _brandings.FindAsync(KioskBranding.DefaultId, cancellationToken).ConfigureAwait(false);

        return new KioskBrandingView(
            id == KioskBranding.DefaultId ? null : id,
            Texts(own),
            id == KioskBranding.DefaultId ? null : Texts(fallback),
            Info(own?.Image),
            Info(fallback?.Image),
            KioskBranding.Keys,
            KioskBranding.MaxLength,
            own?.UpdatedAt);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts(KioskBranding? branding)
        => branding is null
            ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            : branding.Texts.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static KioskPhotoInfo? Info(KioskPhoto? image)
        => image is null ? null : new KioskPhotoInfo(image.Version, image.ContentType, image.UpdatedAt);

    /// <summary>The default reaches every kiosk; a kiosk's own reaches that kiosk.</summary>
    private Task TellAsync(Guid id, CancellationToken cancellationToken)
        => id == KioskBranding.DefaultId
            ? _notifier.AllKiosksConfigChangedAsync(cancellationToken)
            : _notifier.KioskConfigChangedAsync(id, cancellationToken);

    private async Task<KioskPhotoContent> ReadAsync(KioskPhoto image, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await _storage.ReadAsync(ToStored(image.Object), StorageBucket.Files, buffer, cancellationToken).ConfigureAwait(false);
        return new KioskPhotoContent(buffer.ToArray(), image.ContentType, image.Version);
    }

    /// <summary>A delete that fails leaves an unreferenced encrypted object, never a kiosk pointing at nothing.</summary>
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
            _logger.LogWarning(exception, "A replaced kiosk branding image could not be deleted ({StorageKey})", previous.Object.Key);
        }
    }

    private static StoredObject ToStored(FileObject stored) => new(
        stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId);

    private static string? Snapshot(KioskPhoto? image) => image is null
        ? null
        : JsonSerializer.Serialize(new { image.ContentType, image.Version, Bytes = image.Object.PlaintextLength });

    private Task AuditAsync(Guid adminUserId, string action, Guid id, string? before, string? after, string? ip, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminUserId, action, "KioskBranding", id == KioskBranding.DefaultId ? "default" : id.ToString(), before, after, ip, Reason: null),
            cancellationToken);

    private static ValidationException Invalid(string key) => new([new ValidationError("image", key)]);
}
