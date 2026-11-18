using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Support;

namespace NeoPrint.Application.Support;

/// <summary>
/// A picture added to one's own claim in the app (T-438): JPEG, PNG or WebP by
/// its bytes, at most <see cref="ClaimPhotoHandlers.MaxBytes"/>; the app sends
/// it shrunk.
/// </summary>
public sealed record AddMyClaimPhotoCommand(Guid UserId, Guid ClaimId, byte[] Content) : IRequest<SupportClaimView>;

/// <summary>A claim's picture for the panel, by its place on the claim (0-based).</summary>
public sealed record GetClaimPhotoQuery(Guid AdminUserId, Guid ClaimId, int Index) : IRequest<KioskPhotoContent?>;

public sealed class ClaimPhotoHandlers :
    IRequestHandler<AddMyClaimPhotoCommand, SupportClaimView>,
    IRequestHandler<GetClaimPhotoQuery, KioskPhotoContent?>
{
    /// <summary>A phone picture as the app sends it (shrunk to 1600 px) is far below this.</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    private readonly ISupportClaimRepository _claims;
    private readonly IFileStorage _storage;
    private readonly IPermissionResolver _permissions;
    private readonly IClock _clock;
    private readonly ILogger<ClaimPhotoHandlers> _logger;

    public ClaimPhotoHandlers(
        ISupportClaimRepository claims,
        IFileStorage storage,
        IPermissionResolver permissions,
        IClock clock,
        ILogger<ClaimPhotoHandlers> logger)
    {
        _claims = claims;
        _storage = storage;
        _permissions = permissions;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SupportClaimView> Handle(AddMyClaimPhotoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content.Length == 0)
        {
            throw Invalid("support.photoEmpty");
        }

        if (request.Content.Length > MaxBytes)
        {
            throw new ValidationException([new ValidationError("image", "support.photoTooLarge", ("maxMb", MaxBytes / (1024 * 1024)))]);
        }

        // The type from the bytes, never from the request.
        var type = KioskPhotoHandlers.ImageType(request.Content) ?? throw Invalid("support.photoNotImage");

        var claim = await _claims.FindByIdAsync(request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null || claim.UserId != request.UserId)
        {
            throw new NotFoundException("claim");
        }

        if (claim.Status == ClaimStatus.Closed)
        {
            throw new DomainException("support.claimClosed");
        }

        if (claim.Photos.Count >= SupportClaim.MaxPhotos)
        {
            throw new DomainException(UserMessage.Of("support.photoLimit", ("max", SupportClaim.MaxPhotos)));
        }

        var now = _clock.UtcNow;
        StoredObject stored;
        using (var content = new MemoryStream(request.Content, writable: false))
        {
            stored = await _storage
                .WriteAsync(StorageBucket.Files, $"claim-photos/{claim.Id:N}/photo-{now.Ticks}", content, type, cancellationToken)
                .ConfigureAwait(false);
        }

        claim.AddPhoto(
            new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
            type,
            now);

        try
        {
            await _claims.UpdateAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not on the claim: the object would be kept for nothing.
            await DeleteQuietlyAsync(stored, cancellationToken).ConfigureAwait(false);
            throw;
        }

        return SupportClaimView.From(claim) with { Contact = null };
    }

    public async Task<KioskPhotoContent?> Handle(GetClaimPhotoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var claim = await _claims.FindByIdAsync(request.ClaimId, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            return null;
        }

        // Only within the account's kiosks (T-301), as the claim itself.
        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        if (!effective.AllKiosks && (claim.KioskId is not { } kioskId || !effective.CanSee(kioskId)))
        {
            return null;
        }

        if (request.Index < 0 || request.Index >= claim.Photos.Count)
        {
            return null;
        }

        var photo = claim.Photos[request.Index];
        using var buffer = new MemoryStream();
        await _storage.ReadAsync(ToStored(photo.Object), StorageBucket.Files, buffer, cancellationToken).ConfigureAwait(false);
        return new KioskPhotoContent(buffer.ToArray(), photo.ContentType, photo.AddedAt.Ticks);
    }

    private async Task DeleteQuietlyAsync(StoredObject stored, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.DeleteAsync(stored, StorageBucket.Files, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A claim picture that was not kept could not be deleted ({StorageKey})", stored.Key);
        }
    }

    private static StoredObject ToStored(FileObject stored) => new(
        stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId);

    private static ValidationException Invalid(string key) => new([new ValidationError("image", key)]);
}
