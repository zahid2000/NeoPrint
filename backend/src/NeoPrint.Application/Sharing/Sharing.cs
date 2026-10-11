using System.Text.RegularExpressions;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Application.Notifications;
using NeoPrint.Application.UserQr;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Sharing;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Sharing;

/// <summary>Persistence for <see cref="Share"/> (collection <c>file_shares</c>).</summary>
public interface IShareRepository
{
    Task AddAsync(Share share, CancellationToken cancellationToken = default);

    Task<Share?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Share share, CancellationToken cancellationToken = default);

    /// <summary>What the user sent, newest first.</summary>
    Task<IReadOnlyList<Share>> ListSentAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>What the user received, newest first.</summary>
    Task<IReadOnlyList<Share>> ListReceivedAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>An unsaved share of this very PDF to this person: sending it again is the same share.</summary>
    Task<Share?> FindUnsavedAsync(Guid fileId, Guid toUserId, string pdfKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>POST shares</c> (plan sections 6, 8.2): sends the file as it is now to
/// the user with <paramref name="ToPublicId"/>.
/// </summary>
/// <param name="ConfirmSensitive">The sender saw the warning a personal document gets and went on.</param>
public sealed record SendShareCommand(Guid UserId, Guid FileId, string? ToPublicId, string? Message, bool ConfirmSensitive)
    : IRequest<ShareView>;

/// <summary><c>GET shares/sent</c>.</summary>
public sealed record ListSentSharesQuery(Guid UserId) : IRequest<IReadOnlyList<ShareView>>;

/// <summary><c>GET shares/received</c>: the "Alınanlar" list.</summary>
public sealed record ListReceivedSharesQuery(Guid UserId) : IRequest<IReadOnlyList<ShareView>>;

/// <summary><c>POST shares/{id}/save</c>: "Fayllarıma saxla" - the recipient's own copy.</summary>
public sealed record SaveShareCommand(Guid UserId, Guid ShareId) : IRequest<UserFileResult>;

/// <summary><c>GET shares/{id}/pages/{n}.png</c>: the recipient's preview before saving.</summary>
public sealed record GetSharePageImageQuery(Guid UserId, Guid ShareId, int Page, int? Width, string? IfNoneMatch)
    : IRequest<FilePageImage>;

/// <summary><c>GET shares/qr.png</c>: the QR others scan to send this user a file.</summary>
public sealed record GetMyShareQrQuery(Guid UserId) : IRequest<byte[]>;

/// <summary>One share in a list.</summary>
/// <param name="OtherPublicId">The recipient's id in "sent", the sender's in "received".</param>
/// <param name="Availability">
/// <c>Available</c>, <c>Saved</c> (the recipient has their copy), <c>Gone</c>
/// (the sender deleted it) or <c>Changed</c> (the sender changed it after sending).
/// </param>
/// <param name="SavedFileId">The recipient's copy; only in "received", and only while it exists.</param>
public sealed record ShareView(
    Guid Id,
    string FileName,
    int PageCount,
    bool IsSensitive,
    string? Message,
    string OtherPublicId,
    DateTime CreatedAt,
    string Availability,
    Guid? SavedFileId,
    DateTime? SavedAt);

/// <summary>Sending, listing, previewing and saving shared files (docs/DECISIONS.md 2026-10-05, sharing).</summary>
public sealed partial class ShareHandlers :
    IRequestHandler<SendShareCommand, ShareView>,
    IRequestHandler<ListSentSharesQuery, IReadOnlyList<ShareView>>,
    IRequestHandler<ListReceivedSharesQuery, IReadOnlyList<ShareView>>,
    IRequestHandler<SaveShareCommand, UserFileResult>,
    IRequestHandler<GetSharePageImageQuery, FilePageImage>,
    IRequestHandler<GetMyShareQrQuery, byte[]>
{
    /// <summary>How many shares a list returns, newest first.</summary>
    public const int ListSize = 100;

    /// <summary>What the share QR holds: the app opens the share screen with the recipient filled in.</summary>
    public const string QrPrefix = "neoprint://share/";

    private readonly IShareRepository _shares;
    private readonly IFileRepository _files;
    private readonly IUserRepository _users;
    private readonly IFileStorage _storage;
    private readonly IPageRenderer _renderer;
    private readonly IPagePreviewCache _previews;
    private readonly FileIntake _intake;
    private readonly IQrImages _qr;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<ShareHandlers> _logger;

    public ShareHandlers(
        IShareRepository shares,
        IFileRepository files,
        IUserRepository users,
        IFileStorage storage,
        IPageRenderer renderer,
        IPagePreviewCache previews,
        FileIntake intake,
        IQrImages qr,
        ISender sender,
        IClock clock,
        ILogger<ShareHandlers> logger)
    {
        _shares = shares;
        _files = files;
        _users = users;
        _storage = storage;
        _renderer = renderer;
        _previews = previews;
        _intake = intake;
        _qr = qr;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    // NP- and six of User.RandomCode's letters and digits.
    [GeneratedRegex("^NP-[A-Z0-9]{6}$")]
    private static partial Regex PublicIdFormat();

    public async Task<ShareView> Handle(SendShareCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await _files.FindByIdAsync(request.FileId, cancellationToken).ConfigureAwait(false);
        if (file is null || file.UserId != request.UserId || file.DeletedAt is not null)
        {
            throw new NotFoundException("file");
        }

        string? message;
        try
        {
            message = Share.CleanMessage(request.Message);
        }
        catch (DomainException exception)
        {
            throw Invalid("message", exception.Text);
        }

        var publicId = (request.ToPublicId ?? string.Empty).Trim().ToUpperInvariant();
        if (!PublicIdFormat().IsMatch(publicId))
        {
            throw Invalid("toPublicId", new UserMessage("shares.publicIdInvalid"));
        }

        // Blocked and deleted accounts read as unknown: whether an id was
        // blocked is not the sender's business.
        var recipient = await _users.FindByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);
        if (recipient is not { Status: UserStatus.Active })
        {
            throw new NotFoundException("shareRecipient");
        }

        if (recipient.Id == request.UserId)
        {
            throw Invalid("toPublicId", new UserMessage("shares.toSelf"));
        }

        if (file.IsSensitive && !request.ConfirmSensitive)
        {
            throw Invalid("confirmSensitive", UserMessage.Of("shares.sensitiveNeedsConfirmation", ("name", file.FileName)));
        }

        var sender = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");

        var share = file.Pdf is { } pdf
            ? await _shares.FindUnsavedAsync(file.Id, recipient.Id, pdf.Key, cancellationToken).ConfigureAwait(false)
            : null;

        if (share is null)
        {
            share = Share.Send(file, recipient.Id, message, _clock.UtcNow);
            await _shares.AddAsync(share, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "User {UserId} shared file {FileId} with user {RecipientId} (share {ShareId}).",
                request.UserId,
                file.Id,
                recipient.Id,
                share.Id);
        }

        // Also for a repeat: a notification lost the first time goes now, and
        // its key keeps one that was not lost from going twice.
        await _sender
            .Send(
                new NotifyUserCommand(
                    recipient.Id,
                    NotificationType.FileShared,
                    UserMessage.Of("notifications.fileShared.title"),
                    UserMessage.Of("notifications.fileShared.body", ("from", sender.PublicId), ("name", share.FileName)),
                    NotificationLinks.Shares,
                    $"FileShared:{share.Id}"),
                cancellationToken)
            .ConfigureAwait(false);

        return View(share, recipient.PublicId, ShareAvailability.Available.ToString(), savedFileId: null);
    }

    public async Task<IReadOnlyList<ShareView>> Handle(ListSentSharesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var shares = await _shares.ListSentAsync(request.UserId, ListSize, cancellationToken).ConfigureAwait(false);
        var people = await PublicIdsAsync(shares.Select(share => share.ToUserId), cancellationToken).ConfigureAwait(false);
        var views = new List<ShareView>(shares.Count);

        foreach (var share in shares)
        {
            // The sender sees whether it was saved, not the recipient's copy.
            var availability = share.SavedAt is not null
                ? "Saved"
                : share.AvailabilityOf(await _files.FindByIdAsync(share.FileId, cancellationToken).ConfigureAwait(false)).ToString();

            views.Add(View(share, people.GetValueOrDefault(share.ToUserId, string.Empty), availability, savedFileId: null));
        }

        return views;
    }

    public async Task<IReadOnlyList<ShareView>> Handle(ListReceivedSharesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var shares = await _shares.ListReceivedAsync(request.UserId, ListSize, cancellationToken).ConfigureAwait(false);
        var people = await PublicIdsAsync(shares.Select(share => share.FromUserId), cancellationToken).ConfigureAwait(false);
        var views = new List<ShareView>(shares.Count);

        foreach (var share in shares)
        {
            var copy = await SavedCopyAsync(share, cancellationToken).ConfigureAwait(false);
            var availability = copy is not null
                ? "Saved"
                : (await AvailabilityAsync(share, cancellationToken).ConfigureAwait(false)).Availability.ToString();

            views.Add(View(share, people.GetValueOrDefault(share.FromUserId, string.Empty), availability, copy?.Id));
        }

        return views;
    }

    public async Task<UserFileResult> Handle(SaveShareCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var share = await ReceivedAsync(request.UserId, request.ShareId, cancellationToken).ConfigureAwait(false);

        // Saved before, and the copy is still there: the same copy, not a second.
        if (await SavedCopyAsync(share, cancellationToken).ConfigureAwait(false) is { } saved)
        {
            return UploadFileHandler.Map(saved);
        }

        var (availability, sent) = await AvailabilityAsync(share, cancellationToken).ConfigureAwait(false);
        if (availability != ShareAvailability.Available || sent?.Pdf is not { } pdf)
        {
            throw new DomainException(availability == ShareAvailability.Changed ? "shares.fileChanged" : "shares.fileGone");
        }

        // A save that stopped half way: its copy may be in the files already
        // (only the share was not updated), or not yet - then the same id
        // again, so the objects written last time are overwritten, not doubled.
        var fileId = Guid.NewGuid();
        if (share.SavingFileId is { } earlier)
        {
            var existing = await _files.FindByIdAsync(earlier, cancellationToken).ConfigureAwait(false);
            if (existing is { DeletedAt: null } && existing.UserId == request.UserId)
            {
                share.CompleteSave(_clock.UtcNow);
                await _shares.UpdateAsync(share, cancellationToken).ConfigureAwait(false);
                return UploadFileHandler.Map(existing);
            }

            fileId = existing is null ? earlier : fileId;
        }

        var content = sent.CurrentContent;
        var samePdf = content.Key == pdf.Key;
        await _intake
            .EnsureQuotaAsync(request.UserId, content.PlaintextLength + (samePdf ? 0 : pdf.PlaintextLength), cancellationToken)
            .ConfigureAwait(false);

        // Written down before a byte is copied (and refused if another tap got
        // there first: the row version), so a retry reuses this copy.
        share.BeginSave(fileId);
        await _shares.UpdateAsync(share, cancellationToken).ConfigureAwait(false);

        var extension = sent.CurrentExtension;
        var original = await CopyAsync(content, $"files/{request.UserId:N}/{fileId:N}/original.{extension}", sent.MediaType, cancellationToken)
            .ConfigureAwait(false);
        var copiedPdf = samePdf
            ? original
            : await CopyAsync(pdf, $"files/{request.UserId:N}/{fileId:N}/document.pdf", "application/pdf", cancellationToken).ConfigureAwait(false);

        var copy = StoredFile.ReceiveShared(
            fileId,
            request.UserId,
            sent,
            original,
            extension,
            copiedPdf,
            await _intake.DefaultAutoDeleteAsync(sent.IsSensitive, cancellationToken).ConfigureAwait(false),
            _clock.UtcNow);

        await _files.AddAsync(copy, cancellationToken).ConfigureAwait(false);

        share.CompleteSave(_clock.UtcNow);
        await _shares.UpdateAsync(share, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} saved share {ShareId} as file {FileId} ({Bytes} bytes).",
            request.UserId,
            share.Id,
            copy.Id,
            copy.AllObjects().Sum(stored => stored.PlaintextLength));

        return UploadFileHandler.Map(copy);
    }

    public async Task<FilePageImage> Handle(GetSharePageImageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var share = await ReceivedAsync(request.UserId, request.ShareId, cancellationToken).ConfigureAwait(false);
        var (availability, sent) = await AvailabilityAsync(share, cancellationToken).ConfigureAwait(false);

        if (availability != ShareAvailability.Available || sent is null)
        {
            throw new DomainException(availability == ShareAvailability.Changed ? "shares.fileChanged" : "shares.fileGone");
        }

        return await GetFilePageImageHandler
            .RenderAsync(sent, request.Page, request.Width, request.IfNoneMatch, _storage, _renderer, _previews, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<byte[]> Handle(GetMyShareQrQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");

        return _qr.Png(QrPrefix + user.PublicId);
    }

    // Someone else's share is "not found": the answer does not confirm it exists.
    private async Task<Share> ReceivedAsync(Guid userId, Guid shareId, CancellationToken cancellationToken)
        => await _shares.FindByIdAsync(shareId, cancellationToken).ConfigureAwait(false) is { } share && share.ToUserId == userId
            ? share
            : throw new NotFoundException("share");

    private async Task<StoredFile?> SavedCopyAsync(Share share, CancellationToken cancellationToken)
        => share.SavedFileId is { } id
            && await _files.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) is { DeletedAt: null } copy
            && copy.UserId == share.ToUserId
                ? copy
                : null;

    /// <summary>The sender's file and whether it is still what was sent; a sender no longer active takes it with them.</summary>
    private async Task<(ShareAvailability Availability, StoredFile? File)> AvailabilityAsync(Share share, CancellationToken cancellationToken)
    {
        var sender = await _users.FindByIdAsync(share.FromUserId, cancellationToken).ConfigureAwait(false);
        if (sender is not { Status: UserStatus.Active })
        {
            return (ShareAvailability.Gone, null);
        }

        var file = await _files.FindByIdAsync(share.FileId, cancellationToken).ConfigureAwait(false);
        return (share.AvailabilityOf(file), file);
    }

    private async Task<FileObject> CopyAsync(FileObject source, string key, string contentType, CancellationToken cancellationToken)
    {
        await using var content = await _storage
            .OpenReadAsync(
                new StoredObject(source.ProviderId, source.Key, source.Sha256, source.EncryptedLength, source.PlaintextLength, source.KekId),
                StorageBucket.Files,
                cancellationToken)
            .ConfigureAwait(false);

        var stored = await _storage
            .WriteAsync(StorageBucket.Files, key, content, contentType, cancellationToken)
            .ConfigureAwait(false);

        return UploadFileHandler.ToFileObject(stored);
    }

    private async Task<Dictionary<Guid, string>> PublicIdsAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<Guid, string>();

        foreach (var id in userIds.Distinct())
        {
            if (await _users.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) is { } user)
            {
                ids[id] = user.PublicId;
            }
        }

        return ids;
    }

    private static ShareView View(Share share, string otherPublicId, string availability, Guid? savedFileId) => new(
        share.Id,
        share.FileName,
        share.PageCount,
        share.IsSensitive,
        share.Message,
        otherPublicId,
        share.CreatedAt,
        availability,
        savedFileId,
        share.SavedAt);

    private static ValidationException Invalid(string field, UserMessage message) => new([new ValidationError(field, message)]);
}
