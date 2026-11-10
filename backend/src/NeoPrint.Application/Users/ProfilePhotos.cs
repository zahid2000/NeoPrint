using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Users;

/// <summary>
/// The user's profile picture (T-396; docs/DECISIONS.md 2026-11-11): only its
/// owner sees it for now; it goes into "Məlumatlarımı yüklə"; a deleted
/// account's is erased with its files.
/// </summary>
/// <remarks>
/// A photo straight from a phone camera is taken as it is, up to
/// <see cref="ProfilePhotoHandlers.MaxBytes"/> - the owner's choice: no
/// squeezing at upload; making it smaller is for later, inside. The API
/// trusts only the bytes: a JPEG, PNG or WebP signature, the type read from
/// them, never from the request (as for kiosk pictures, ADR-0005).
/// </remarks>
public sealed record SetMyPhotoCommand(Guid UserId, byte[] Content) : IRequest<MyPhotoInfo>;

/// <summary>Takes the picture away; the app shows the initials again.</summary>
public sealed record RemoveMyPhotoCommand(Guid UserId) : IRequest;

/// <summary>The picture itself, for its owner; null when there is none.</summary>
public sealed record GetMyPhotoQuery(Guid UserId) : IRequest<MyPhotoContent?>;

/// <param name="Version">Changes with every new picture: the app caches by it.</param>
public sealed record MyPhotoInfo(long Version, string ContentType, DateTime UpdatedAt);

public sealed record MyPhotoContent(byte[] Bytes, string ContentType, long Version);

public sealed class ProfilePhotoHandlers :
    IRequestHandler<SetMyPhotoCommand, MyPhotoInfo>,
    IRequestHandler<RemoveMyPhotoCommand>,
    IRequestHandler<GetMyPhotoQuery, MyPhotoContent?>
{
    /// <summary>Room for a full-size photo from today's phone cameras (50 MP JPEGs run to 15-20 MB).</summary>
    public const int MaxBytes = 25 * 1024 * 1024;

    private readonly IUserRepository _users;
    private readonly IFileStorage _storage;
    private readonly IClock _clock;
    private readonly ILogger<ProfilePhotoHandlers> _logger;

    public ProfilePhotoHandlers(IUserRepository users, IFileStorage storage, IClock clock, ILogger<ProfilePhotoHandlers> logger)
    {
        _users = users;
        _storage = storage;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MyPhotoInfo> Handle(SetMyPhotoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content.Length == 0)
        {
            throw Invalid("users.photoEmpty");
        }

        if (request.Content.Length > MaxBytes)
        {
            throw new ValidationException(
                [new ValidationError("photo", "users.photoTooLarge", ("maxMb", MaxBytes / (1024 * 1024)))]);
        }

        var type = KioskPhotoHandlers.ImageType(request.Content) ?? throw Invalid("users.photoNotImage");
        var user = await LoadAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        // A new key for every picture: an app never gets the old bytes under the new version.
        StoredObject stored;
        using (var content = new MemoryStream(request.Content, writable: false))
        {
            stored = await _storage
                .WriteAsync(StorageBucket.Files, $"profile-photos/{user.Id:N}/photo-{now.Ticks}", content, type, cancellationToken)
                .ConfigureAwait(false);
        }

        var previous = user.ReplacePhoto(
            new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
            type,
            now);

        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);

        return new MyPhotoInfo(user.Photo!.Version, user.Photo.ContentType, user.Photo.UpdatedAt);
    }

    public async Task Handle(RemoveMyPhotoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await LoadAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        // Already gone: the same end, nothing to write.
        if (user.Photo is null)
        {
            return;
        }

        var previous = user.RemovePhoto();
        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
        await DeleteQuietlyAsync(previous, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MyPhotoContent?> Handle(GetMyPhotoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        if (user?.Photo is not { } photo)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await _storage.ReadAsync(Stored(photo.Object), StorageBucket.Files, buffer, cancellationToken).ConfigureAwait(false);

        return new MyPhotoContent(buffer.ToArray(), photo.ContentType, photo.Version);
    }

    /// <summary>The stored object of a picture, for reading, copying or erasing it.</summary>
    public static StoredObject Stored(FileObject stored) => new(
        stored.ProviderId,
        stored.Key,
        stored.Sha256,
        stored.EncryptedLength,
        stored.PlaintextLength,
        stored.KekId);

    private async Task<User> LoadAsync(Guid userId, CancellationToken cancellationToken)
        => await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) is { CanSignIn: true } user
            ? user
            : throw new NotFoundException("user");

    private async Task DeleteQuietlyAsync(UserPhoto? previous, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            await _storage.DeleteAsync(Stored(previous.Object), StorageBucket.Files, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The change itself stands; the object is left for a clean-up.
            _logger.LogWarning(exception, "A replaced profile photo could not be deleted ({StorageKey})", previous.Object.Key);
        }
    }

    private static ValidationException Invalid(string key) => new([new ValidationError("photo", key)]);
}
