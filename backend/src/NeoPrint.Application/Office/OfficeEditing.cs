using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Features;
using NeoPrint.Application.Files;
using NeoPrint.Application.Localization;
using NeoPrint.Contracts;
using NeoPrint.Documents;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Office;

/// <summary>
/// Which files open in the Office editor (plan section 2.2, Addım 9; T-459):
/// Word, Excel and PowerPoint documents and their OpenDocument kin. Plain text
/// has its own editor (T-458); macro-enabled files are left out, so no macro
/// is ever carried through an edit.
/// </summary>
public static class OfficeFormats
{
    public static bool IsEditable(string? extension)
        => FileFormats.Find(extension) is { Route: ConversionRoute.LibreOffice } format
           && !FileFormats.IsPlainText(format.Extension)
           && format.Extension is not ("docm" or "xlsm" or "wps");
}

/// <summary>Collabora Online's discovery: where its editor is, while it runs.</summary>
public interface IOfficeDiscovery
{
    /// <summary>
    /// The editor page for this extension as a path on the public origin
    /// (<c>/browser/{hash}/cool.html?</c>); null when Collabora is not
    /// running or does not edit the format.
    /// </summary>
    Task<string?> EditorPathAsync(string extension, CancellationToken cancellationToken);
}

/// <summary>One person editing one file, for <see cref="OfficeSessions.Lifetime"/>.</summary>
public sealed record OfficeSession(Guid FileId, Guid UserId, string UserName, DateTime ExpiresAt);

/// <summary>
/// The WOPI access tokens (plan: "30 dəq-lik WOPI access token"). A token is
/// random and opaque; only its hash is kept, so a copy of the store opens nothing.
/// </summary>
public interface IOfficeSessionStore
{
    /// <returns>The token to hand to Collabora.</returns>
    Task<string> CreateAsync(OfficeSession session, CancellationToken cancellationToken);

    /// <summary>The session while it lasts; null for an unknown or expired token.</summary>
    Task<OfficeSession?> FindAsync(string? token, CancellationToken cancellationToken);
}

public static class OfficeSessions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
}

/// <summary><c>POST files/{id}/office-session</c>: opens a file in the Office editor.</summary>
public sealed record StartOfficeEditCommand(Guid UserId, Guid FileId) : IRequest<OfficeEditStart>;

/// <param name="EditorPath">The editor page on the API's own origin, <c>WOPISrc</c> filled in.</param>
/// <param name="AccessToken">Posted to the page as <c>access_token</c>, never put in its URL.</param>
/// <param name="AccessTokenTtl">When the token stops working, in Unix milliseconds (as WOPI wants it).</param>
public sealed record OfficeEditStart(string EditorPath, string AccessToken, long AccessTokenTtl);

/// <summary>WOPI <c>CheckFileInfo</c>.</summary>
public sealed record WopiFileInfoQuery(string? Token, Guid FileId) : IRequest<WopiFileInfo?>;

/// <summary>What Collabora needs to know of a file and of who edits it.</summary>
public sealed record WopiFileInfo(
    string BaseFileName,
    long Size,
    string OwnerId,
    string UserId,
    string UserFriendlyName,
    bool UserCanWrite,
    string Version,
    DateTime LastModifiedTime);

/// <summary>WOPI <c>GetFile</c>: the file as it is now.</summary>
public sealed record WopiGetFileQuery(string? Token, Guid FileId) : IRequest<WopiContent?>;

public sealed record WopiContent(Stream Content, string MediaType);

/// <summary>WOPI <c>PutFile</c>: a save in the editor.</summary>
public sealed record WopiPutFileCommand(string? Token, Guid FileId, Stream Content) : IRequest<WopiPutResult>;

public enum WopiPutResult
{
    Saved,

    /// <summary>No session, or not for this file: 401.</summary>
    Unauthorized,

    /// <summary>The file cannot take an edit now (an open order, gone): 409.</summary>
    Refused,
}

/// <summary>
/// The Office editor (T-459): a session for the app or the kiosk, and the WOPI
/// host Collabora reads and writes the file through.
/// </summary>
/// <remarks>
/// <para>
/// A save is a new version in the file's own format, never a change to the
/// original; the file is converted again, as after any edit (plan section 2.2).
/// </para>
/// <para>
/// The WOPI routes are reached by Collabora inside the stack only - the panel's
/// nginx does not pass <c>/wopi</c> - and every call needs the session's token.
/// </para>
/// </remarks>
public sealed class OfficeEditHandlers :
    IRequestHandler<StartOfficeEditCommand, OfficeEditStart>,
    IRequestHandler<WopiFileInfoQuery, WopiFileInfo?>,
    IRequestHandler<WopiGetFileQuery, WopiContent?>,
    IRequestHandler<WopiPutFileCommand, WopiPutResult>
{
    /// <summary>How Collabora reaches the API's WOPI routes, inside the stack.</summary>
    private readonly OfficeOptions _options;
    private readonly IFileRepository _files;
    private readonly IUserRepository _users;
    private readonly IFileStorage _storage;
    private readonly IOfficeDiscovery _discovery;
    private readonly IOfficeSessionStore _sessions;
    private readonly IFeatureFlagEvaluator _features;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly PrintJobs.IPrintJobRepository _jobs;
    private readonly IClock _clock;
    private readonly ILogger<OfficeEditHandlers> _logger;

    public OfficeEditHandlers(
        OfficeOptions options,
        IFileRepository files,
        IUserRepository users,
        IFileStorage storage,
        IOfficeDiscovery discovery,
        IOfficeSessionStore sessions,
        IFeatureFlagEvaluator features,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        PrintJobs.IPrintJobRepository jobs,
        IClock clock,
        ILogger<OfficeEditHandlers> logger)
    {
        _options = options;
        _files = files;
        _users = users;
        _storage = storage;
        _discovery = discovery;
        _sessions = sessions;
        _features = features;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
    }

    public async Task<OfficeEditStart> Handle(StartOfficeEditCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _features.RequireAsync(FeatureKeys.OfficeEditor, null, request.UserId, cancellationToken).ConfigureAwait(false);

        var file = await Files.FileAccess.LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        if (file.EditLocked)
        {
            throw Invalid("files.edit.scanLocked");
        }

        if (file.Source == FileSource.Layout)
        {
            throw Invalid("files.edit.useLayoutEditor");
        }

        // The file as it is now: a page edit made a PDF of it, and a PDF is
        // not opened as a document.
        if (!OfficeFormats.IsEditable(file.CurrentExtension))
        {
            throw Invalid("office.notOffice");
        }

        if (file.Status != FileStatus.Ready)
        {
            throw Invalid("files.edit.notReady");
        }

        if (await _jobs.HasOpenOrderForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("files.inUseByOrder");
        }

        var editor = await _discovery.EditorPathAsync(file.CurrentExtension, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException("office.unavailable");

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");

        var expires = _clock.UtcNow + OfficeSessions.Lifetime;
        var token = await _sessions
            .CreateAsync(new OfficeSession(file.Id, user.Id, user.FullName ?? user.PublicId, expires), cancellationToken)
            .ConfigureAwait(false);

        var source = Uri.EscapeDataString($"{_options.WopiBaseUrl.TrimEnd('/')}/wopi/files/{file.Id:N}");

        _logger.LogInformation("User {UserId} opened file {FileId} in the Office editor.", user.Id, file.Id);

        return new OfficeEditStart(
            $"{editor}WOPISrc={source}",
            token,
            new DateTimeOffset(expires, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    public async Task<WopiFileInfo?> Handle(WopiFileInfoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await OpenAsync(request.Token, request.FileId, cancellationToken).ConfigureAwait(false) is not var (session, file))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(file.FileName);

        return new WopiFileInfo(
            $"{stem}.{file.CurrentExtension}",
            file.CurrentContent.PlaintextLength,
            file.UserId.ToString("N"),
            session.UserId.ToString("N"),
            session.UserName,
            UserCanWrite: true,
            // Changes with every save, so Collabora knows its copy is current.
            Version: (file.LatestVersion?.Number ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            file.LatestVersion?.CreatedAt ?? file.CreatedAt);
    }

    public async Task<WopiContent?> Handle(WopiGetFileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await OpenAsync(request.Token, request.FileId, cancellationToken).ConfigureAwait(false) is not var (_, file))
        {
            return null;
        }

        var stream = await _storage
            .OpenReadAsync(EditFileHandler.ToStored(file.CurrentContent), StorageBucket.Files, cancellationToken)
            .ConfigureAwait(false);

        return new WopiContent(stream, FileFormats.Find(file.CurrentExtension)?.MediaType ?? "application/octet-stream");
    }

    public async Task<WopiPutResult> Handle(WopiPutFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await OpenAsync(request.Token, request.FileId, cancellationToken).ConfigureAwait(false) is not var (session, file))
        {
            return WopiPutResult.Unauthorized;
        }

        // Ordered while the editor was open: the order keeps the pages it was priced on.
        if (file.EditLocked
            || await _jobs.HasOpenOrderForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("A save of file {FileId} was refused: the file cannot be edited now.", file.Id);
            return WopiPutResult.Refused;
        }

        var extension = file.CurrentExtension;
        var format = FileFormats.Find(extension)!;
        var number = (file.LatestVersion?.Number ?? 0) + 1;

        var stored = await _storage.WriteAsync(
                StorageBucket.Files,
                $"files/{file.UserId:N}/{file.Id:N}/v{number}.{extension}",
                request.Content,
                format.MediaType,
                cancellationToken)
            .ConfigureAwait(false);

        var keep = await _settings.GetIntAsync(SettingKeys.FilesVersionsToKeep, 5, cancellationToken).ConfigureAwait(false);

        var dropped = file.ApplyEdit(
            new FileObject(
                stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
            extension,
            new UserMessage("files.note.officeEdited").Encode(),
            Math.Max(1, keep),
            ActorContext.User(session.UserId, "files.reason.edited"),
            _clock.UtcNow);

        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        await EditFileHandler.DeleteDroppedAsync(_storage, _logger, file, dropped, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("File {FileId} was saved from the Office editor: version {Version}.", file.Id, number);

        // As after any edit: converted again, so pages and price follow it.
        await _events.PublishAsync(new FileUploaded(file.Id, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        return WopiPutResult.Saved;
    }

    /// <summary>The session and its file; null when the token is not for this file.</summary>
    private async Task<(OfficeSession Session, StoredFile File)?> OpenAsync(
        string? token, Guid fileId, CancellationToken cancellationToken)
    {
        var session = await _sessions.FindAsync(token, cancellationToken).ConfigureAwait(false);

        if (session is null || session.FileId != fileId)
        {
            return null;
        }

        var file = await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false);

        return file is null || file.UserId != session.UserId || file.DeletedAt is not null
            ? null
            : (session, file);
    }

    private static ValidationException Invalid(string key) => new([new ValidationError("edit", key)]);
}

/// <summary>Where the stack's Collabora is (configuration <c>Office</c>).</summary>
public sealed class OfficeOptions
{
    public const string Section = "Office";

    /// <summary>Collabora inside the stack, for its discovery; empty turns the editor off.</summary>
    public string CollaboraUrl { get; set; } = string.Empty;

    /// <summary>The API as Collabora reaches it, for <c>WOPISrc</c>.</summary>
    public string WopiBaseUrl { get; set; } = "http://api:8080";
}
