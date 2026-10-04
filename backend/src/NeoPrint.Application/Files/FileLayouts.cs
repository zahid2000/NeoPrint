using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Contracts;
using NeoPrint.Documents;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Files;

// ---------------------------------------------------------------------------
// Scans and page layouts (plan section 2.11; docs/DECISIONS.md 2026-10-05):
// POST files/scan, POST files/layout, PUT files/{id}/layout, GET files/{id}/layout
// ---------------------------------------------------------------------------

/// <summary>What a layout uses, read from its JSON without drawing it.</summary>
/// <param name="Images">Every picture name, once each.</param>
/// <param name="Paper">A4, A3 or A5.</param>
public sealed record LayoutOutline(IReadOnlyList<string> Images, int PageCount, string Paper);

/// <summary>A scanner document type and its real size (plan section 2.11).</summary>
public sealed record ScanDocumentType(string Key, double WidthMm, double HeightMm, bool Sensitive);

/// <summary>
/// Reads and draws page layouts - <c>NeoPrint.Layout</c>, the renderer the
/// kiosk uses too, behind an interface so this layer does not depend on the
/// PDF and imaging libraries.
/// </summary>
public interface ILayoutDocuments
{
    /// <summary>Checks a layout's JSON.</summary>
    /// <exception cref="ValidationException">It is malformed or cannot be drawn.</exception>
    LayoutOutline Read(string layoutJson);

    /// <summary>The same layout with its pictures renamed.</summary>
    string RenameImages(string layoutJson, IReadOnlyDictionary<string, string> names);

    /// <summary>The scanner's document type with this key, or null.</summary>
    ScanDocumentType? FindDocumentType(string key);

    /// <summary>Draws the layout as a PDF at real size.</summary>
    /// <param name="images">The bytes of each picture, by name.</param>
    /// <exception cref="ValidationException">A picture is missing, unreadable or too large.</exception>
    Task RenderAsync(
        string layoutJson, Func<string, byte[]?> images, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>One picture of a scan, as the phone prepared it.</summary>
/// <param name="Name">The name the layout uses for it.</param>
public sealed record ScanPicture(string Name, byte[] Content);

/// <summary>
/// Saves a scan: the pictures, arranged on paper, become a finished PDF that
/// is printed but never edited (docs/DECISIONS.md 2026-10-05). The pictures
/// themselves are not kept.
/// </summary>
/// <param name="DocumentType">A <see cref="ScanDocumentType"/> key, or <c>Custom</c> with the size given.</param>
public sealed record CreateScanCommand(
    Guid UserId,
    IReadOnlyList<ScanPicture> Pictures,
    string LayoutJson,
    string? DocumentType,
    double? PhysicalWidthMm,
    double? PhysicalHeightMm,
    bool IsSensitive,
    string? FileName) : IRequest<UserFileResult>;

/// <summary>
/// Saves a new layout (plan section 2.11). Its pictures are named
/// <c>file:{id}</c> - an image file's current content - or
/// <c>file:{id}:{page}</c> - a page of a file's PDF.
/// </summary>
public sealed record CreateLayoutCommand(Guid UserId, string LayoutJson, string? FileName) : IRequest<UserFileResult>;

/// <summary>
/// Saves a changed layout as a new version. Pictures it already holds are
/// named <c>asset:{key}</c>; new ones as in <see cref="CreateLayoutCommand"/>.
/// </summary>
public sealed record UpdateLayoutCommand(Guid UserId, Guid FileId, string LayoutJson) : IRequest<UserFileResult>;

/// <summary>A layout file's layout, for the editor to open again.</summary>
public sealed record GetLayoutQuery(Guid UserId, Guid FileId) : IRequest<LayoutView>;

/// <summary>A picture a layout holds, without its bytes.</summary>
public sealed record LayoutAssetView(string Name, string MediaType, long SizeBytes);

/// <param name="LayoutJson">Pictures named <c>asset:{key}</c>.</param>
public sealed record LayoutView(Guid FileId, string LayoutJson, IReadOnlyList<LayoutAssetView> Assets);

public sealed class FileLayoutHandlers :
    IRequestHandler<CreateScanCommand, UserFileResult>,
    IRequestHandler<CreateLayoutCommand, UserFileResult>,
    IRequestHandler<UpdateLayoutCommand, UserFileResult>,
    IRequestHandler<GetLayoutQuery, LayoutView>
{
    /// <summary>The most pictures one scan may have - one per page of the longest layout.</summary>
    public const int MaxScanPictures = 50;

    /// <summary>A PDF page used as a picture is drawn this wide: A4 at 300 dpi.</summary>
    public const int PageImageWidth = 2480;

    public const string AssetPrefix = "asset:";
    public const string FilePrefix = "file:";

    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly FileIntake _intake;
    private readonly ILayoutDocuments _layouts;
    private readonly IPageRenderer _pages;
    private readonly ILegalDocumentRepository _legal;
    private readonly ISettingsReader _settings;
    private readonly PrintJobs.IPrintJobRepository _jobs;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<FileLayoutHandlers> _logger;

    public FileLayoutHandlers(
        IFileRepository files,
        IFileStorage storage,
        FileIntake intake,
        ILayoutDocuments layouts,
        IPageRenderer pages,
        ILegalDocumentRepository legal,
        ISettingsReader settings,
        PrintJobs.IPrintJobRepository jobs,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<FileLayoutHandlers> logger)
    {
        _files = files;
        _storage = storage;
        _intake = intake;
        _layouts = layouts;
        _pages = pages;
        _legal = legal;
        _settings = settings;
        _jobs = jobs;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserFileResult> Handle(CreateScanCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureScanNoticeAcceptedAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        var pictures = CheckPictures(request.Pictures);
        var maxBytes = await _intake.MaxBytesAsync(null, cancellationToken).ConfigureAwait(false);

        FileIntake.EnsureWithin(pictures.Values.Sum(bytes => (long)bytes.Length), maxBytes);

        var outline = _layouts.Read(request.LayoutJson);
        var missing = outline.Images.FirstOrDefault(name => !pictures.ContainsKey(name));

        if (missing is not null)
        {
            throw FileIntake.Invalid("layout", "scan.pictureMissing", ("name", missing));
        }

        var (documentType, width, height, presetSensitive) = DocumentSize(request);
        var sensitive = request.IsSensitive || presetSensitive;

        var file = new StoredFile
        {
            UserId = request.UserId,
            FileName = FileIntake.SafeNameWithExtension(request.FileName, $"scan-{_clock.UtcNow:yyyyMMdd-HHmm}", "pdf"),
            Extension = "pdf",
            MediaType = "application/pdf",
            Source = FileSource.Scan,
            IsSensitive = sensitive,
            EditLocked = true,
            DocumentType = documentType,
            PhysicalWidthMm = width,
            PhysicalHeightMm = height,
            LayoutPaper = outline.Paper,
            AutoDelete = await _intake.DefaultAutoDeleteAsync(sensitive, cancellationToken).ConfigureAwait(false),
        };

        file.Original = await RenderAndStoreAsync(
                file, request.LayoutJson, name => pictures.GetValueOrDefault(name), "original.pdf", maxBytes, 0, cancellationToken)
            .ConfigureAwait(false);

        await AddAsync(file, [], cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} saved scan {FileId}: {Pictures} pictures on {Pages} pages, {DocumentType}.",
            request.UserId,
            file.Id,
            pictures.Count,
            outline.PageCount,
            documentType);

        return UploadFileHandler.Map(file);
    }

    public async Task<UserFileResult> Handle(CreateLayoutCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outline = _layouts.Read(request.LayoutJson);
        var maxBytes = await _intake.MaxBytesAsync(null, cancellationToken).ConfigureAwait(false);
        var file = new StoredFile
        {
            UserId = request.UserId,
            FileName = FileIntake.SafeNameWithExtension(request.FileName, $"layout-{_clock.UtcNow:yyyyMMdd-HHmm}", "pdf"),
            Extension = "pdf",
            MediaType = "application/pdf",
            Source = FileSource.Layout,
            LayoutPaper = outline.Paper,
        };

        var pictures = await CollectAsync(file, outline.Images, maxBytes, cancellationToken).ConfigureAwait(false);
        var written = new List<StoredObject>();

        try
        {
            var layoutJson = _layouts.RenameImages(request.LayoutJson, pictures.ToDictionary(p => p.Key, p => p.Value.Name));
            var assets = await StoreNewAssetsAsync(file, pictures.Values, written, cancellationToken).ConfigureAwait(false);
            var newBytes = assets.Sum(asset => asset.Object.PlaintextLength);

            file.IsSensitive = pictures.Values.Any(picture => picture.Sensitive);
            file.AutoDelete = await _intake.DefaultAutoDeleteAsync(file.IsSensitive, cancellationToken).ConfigureAwait(false);
            file.Original = await RenderAndStoreAsync(
                    file, layoutJson, Lookup(pictures.Values), "original.pdf", maxBytes, newBytes, cancellationToken)
                .ConfigureAwait(false);
            written.Add(ToStored(file.Original));
            file.StartLayout(layoutJson, assets);

            await AddAsync(file, [], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(written, file.Id).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "User {UserId} saved layout {FileId}: {Pictures} pictures on {Pages} pages.",
            request.UserId,
            file.Id,
            pictures.Count,
            outline.PageCount);

        return UploadFileHandler.Map(file);
    }

    public async Task<UserFileResult> Handle(UpdateLayoutCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await LoadLayoutAsync(request.UserId, request.FileId, cancellationToken).ConfigureAwait(false);

        if (file.Status != FileStatus.Ready || file.Pdf is null)
        {
            throw FileIntake.Invalid("layout", "files.edit.notReady");
        }

        // An order still to print uses this PDF (as for any edit).
        if (await _jobs.HasOpenOrderForFileAsync(file.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException("files.inUseByOrder");
        }

        var outline = _layouts.Read(request.LayoutJson);
        var maxBytes = await _intake.MaxBytesAsync(null, cancellationToken).ConfigureAwait(false);
        var pictures = await CollectAsync(file, outline.Images, maxBytes, cancellationToken).ConfigureAwait(false);
        var number = (file.LatestVersion?.Number ?? 0) + 1;
        var written = new List<StoredObject>();

        (IReadOnlyList<FileVersion> Versions, IReadOnlyList<LayoutAsset> Assets) dropped;

        try
        {
            var layoutJson = _layouts.RenameImages(request.LayoutJson, pictures.ToDictionary(p => p.Key, p => p.Value.Name));
            var added = await StoreNewAssetsAsync(file, pictures.Values.Where(p => p.Existing is null), written, cancellationToken)
                .ConfigureAwait(false);
            var assets = pictures.Values.Where(p => p.Existing is not null).Select(p => p.Existing!).Concat(added).ToList();

            var pdf = await RenderAndStoreAsync(
                    file,
                    layoutJson,
                    Lookup(pictures.Values),
                    $"v{number}.pdf",
                    maxBytes,
                    added.Sum(asset => asset.Object.PlaintextLength),
                    cancellationToken)
                .ConfigureAwait(false);
            written.Add(ToStored(pdf));

            var keep = await _settings.GetIntAsync(SettingKeys.FilesVersionsToKeep, 5, cancellationToken)
                .ConfigureAwait(false);

            dropped = file.ApplyLayout(
                pdf, layoutJson, assets, Math.Max(1, keep), ActorContext.User(request.UserId, "files.reason.edited"), _clock.UtcNow);

            // Never relaxed: a layout that once held a personal document stays
            // marked, whatever was taken out of it.
            file.IsSensitive |= pictures.Values.Any(picture => picture.Sensitive);
            file.LayoutPaper = outline.Paper;

            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync(written, file.Id).ConfigureAwait(false);
            throw;
        }

        // Not the object the current PDF still reads: the conversion replaces
        // and deletes it (as for an edit).
        await DeleteQuietlyAsync(
                [
                    .. dropped.Versions.Where(version => version.Object.Key != file.Pdf?.Key).Select(version => ToStored(version.Object)),
                    .. dropped.Assets.Select(asset => ToStored(asset.Object)),
                ],
                file.Id)
            .ConfigureAwait(false);

        await _events.PublishAsync(new FileUploaded(file.Id, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Layout {FileId} was changed: version {Version}.", file.Id, number);

        return UploadFileHandler.Map(file);
    }

    public async Task<LayoutView> Handle(GetLayoutQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await LoadLayoutAsync(request.UserId, request.FileId, cancellationToken).ConfigureAwait(false);

        return new LayoutView(
            file.Id,
            file.LayoutProjectJson!,
            [.. file.LayoutAssets.Select(asset => new LayoutAssetView(
                AssetPrefix + asset.Key, asset.MediaType, asset.Object.PlaintextLength))]);
    }

    private async Task<StoredFile> LoadLayoutAsync(Guid userId, Guid fileId, CancellationToken cancellationToken)
    {
        var file = await FileAccess.LoadOwnedAsync(_files, userId, fileId, cancellationToken).ConfigureAwait(false);

        if (file.Source != FileSource.Layout || file.LayoutProjectJson is null)
        {
            throw FileIntake.Invalid("file", "files.layout.notLayout");
        }

        return file;
    }

    /// <summary>
    /// The scan notice is accepted in its current version (docs/DECISIONS.md
    /// 2026-10-05): the proof of consent is on the server, not in the app.
    /// </summary>
    private async Task EnsureScanNoticeAcceptedAsync(Guid userId, CancellationToken cancellationToken)
    {
        var current = await _legal.GetCurrentAsync(LegalDocumentType.ScanNotice, "az", cancellationToken)
            .ConfigureAwait(false);
        var accepted = await _legal.GetAcceptedVersionsAsync(userId, cancellationToken).ConfigureAwait(false);

        if (current is null
            || !accepted.TryGetValue(LegalDocumentType.ScanNotice, out var version)
            || !string.Equals(version, current.Version, StringComparison.Ordinal))
        {
            throw FileIntake.Invalid("consent", "scan.noticeRequired");
        }
    }

    private static Dictionary<string, byte[]> CheckPictures(IReadOnlyList<ScanPicture>? pictures)
    {
        if (pictures is null || pictures.Count == 0)
        {
            throw FileIntake.Invalid("pictures", "scan.noPictures");
        }

        if (pictures.Count > MaxScanPictures)
        {
            throw FileIntake.Invalid("pictures", "scan.tooManyPictures", ("max", MaxScanPictures));
        }

        var byName = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var picture in pictures)
        {
            if (string.IsNullOrWhiteSpace(picture.Name) || !byName.TryAdd(picture.Name, picture.Content))
            {
                throw FileIntake.Invalid("pictures", "scan.pictureName");
            }

            // The phone sends JPEG; whatever arrives is checked by its bytes,
            // not its name (plan section 2.2).
            var header = picture.Content.AsSpan(0, Math.Min(picture.Content.Length, ContentSignature.HeaderLength));

            if (ContentSignature.Identify(header, picture.Name).Format?.Group != FormatGroup.Image)
            {
                throw FileIntake.Invalid("pictures", "scan.notAPicture", ("name", picture.Name));
            }
        }

        return byName;
    }

    private (string Type, double Width, double Height, bool Sensitive) DocumentSize(CreateScanCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentType))
        {
            throw FileIntake.Invalid("documentType", "scan.documentTypeRequired");
        }

        if (request.DocumentType != "Custom")
        {
            var preset = _layouts.FindDocumentType(request.DocumentType)
                         ?? throw FileIntake.Invalid("documentType", "scan.documentTypeUnknown");

            return (preset.Key, preset.WidthMm, preset.HeightMm, preset.Sensitive);
        }

        if (request.PhysicalWidthMm is not (>= 1 and <= 1000) || request.PhysicalHeightMm is not (>= 1 and <= 1000))
        {
            throw FileIntake.Invalid("physicalSize", "scan.physicalSize");
        }

        return ("Custom", request.PhysicalWidthMm.Value, request.PhysicalHeightMm.Value, false);
    }

    /// <summary>A picture for a layout, by the name the request gave it.</summary>
    /// <param name="Name">Its name in the stored layout: <c>asset:{key}</c>.</param>
    /// <param name="Existing">The layout's own copy, when it already had one.</param>
    private sealed record Picture(string Name, byte[] Bytes, string MediaType, bool Sensitive, LayoutAsset? Existing);

    /// <summary>
    /// The bytes of every picture a layout names: its own copies, image files,
    /// and pages of PDFs drawn as pictures.
    /// </summary>
    private async Task<Dictionary<string, Picture>> CollectAsync(
        StoredFile layout, IReadOnlyList<string> names, long maxBytes, CancellationToken cancellationToken)
    {
        var pictures = new Dictionary<string, Picture>(StringComparer.Ordinal);
        long total = 0;

        foreach (var name in names)
        {
            var picture = name.StartsWith(AssetPrefix, StringComparison.Ordinal)
                ? await OwnAssetAsync(layout, name, cancellationToken).ConfigureAwait(false)
                : await FromFileAsync(layout.UserId, name, cancellationToken).ConfigureAwait(false);

            total += picture.Existing is null ? picture.Bytes.Length : 0;
            FileIntake.EnsureWithin(total, maxBytes);

            pictures[name] = picture;
        }

        return pictures;
    }

    private async Task<Picture> OwnAssetAsync(StoredFile layout, string name, CancellationToken cancellationToken)
    {
        var key = name[AssetPrefix.Length..];
        var asset = layout.LayoutAssets.FirstOrDefault(candidate => candidate.Key == key)
                    ?? throw FileIntake.Invalid("layout", "files.layout.imageRef", ("name", name));

        return new Picture(name, await ReadAllAsync(asset.Object, cancellationToken).ConfigureAwait(false), asset.MediaType, asset.Sensitive, asset);
    }

    private async Task<Picture> FromFileAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        if (!TryParseFileReference(name, out var fileId, out var page))
        {
            throw FileIntake.Invalid("layout", "files.layout.imageRef", ("name", name));
        }

        var source = await FileAccess.LoadOwnedAsync(_files, userId, fileId, cancellationToken).ConfigureAwait(false);

        // A saved scan is finished: printed, never taken apart into another
        // layout (docs/DECISIONS.md 2026-10-05).
        if (source.EditLocked)
        {
            throw FileIntake.Invalid("layout", "files.layout.scanNotSource", ("name", source.FileName));
        }

        if (source.Status != FileStatus.Ready || source.Pdf is null)
        {
            throw FileIntake.Invalid("layout", "files.notReadyNamed", ("name", source.FileName));
        }

        var newName = AssetPrefix + Guid.NewGuid().ToString("N");
        var format = FileFormats.Find(source.CurrentExtension);

        if (page is null && format?.Group == FormatGroup.Image)
        {
            return new Picture(
                newName,
                await ReadAllAsync(source.CurrentContent, cancellationToken).ConfigureAwait(false),
                format.MediaType,
                source.IsSensitive,
                null);
        }

        var number = page ?? 1;

        if (number < 1 || number > source.PageCount)
        {
            throw FileIntake.Invalid("layout", "files.pageMissing", ("count", source.PageCount), ("page", number));
        }

        if (!_pages.IsAvailable)
        {
            throw FileIntake.Invalid("layout", "files.previewUnavailable");
        }

        await using var pdf = await _storage.OpenReadAsync(ToStored(source.Pdf), StorageBucket.Files, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var png = await _pages.RenderPngAsync(pdf, number - 1, PageImageWidth, cancellationToken).ConfigureAwait(false);

            return new Picture(newName, png, "image/png", source.IsSensitive, null);
        }
        catch (PageRenderException exception)
        {
            throw new ValidationException([new ValidationError("layout", exception.Text)]);
        }
    }

    /// <summary>Reads <c>file:{id}</c> or <c>file:{id}:{page}</c>.</summary>
    internal static bool TryParseFileReference(string name, out Guid fileId, out int? page)
    {
        fileId = Guid.Empty;
        page = null;

        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name[FilePrefix.Length..].Split(':');

        if (parts.Length is < 1 or > 2 || !Guid.TryParseExact(parts[0], "D", out fileId) && !Guid.TryParseExact(parts[0], "N", out fileId))
        {
            return false;
        }

        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            page = parsed;
        }

        return true;
    }

    private static Func<string, byte[]?> Lookup(IEnumerable<Picture> pictures)
    {
        var byName = pictures.ToDictionary(picture => picture.Name, picture => picture.Bytes, StringComparer.Ordinal);

        return name => byName.GetValueOrDefault(name);
    }

    private async Task<List<LayoutAsset>> StoreNewAssetsAsync(
        StoredFile layout, IEnumerable<Picture> pictures, List<StoredObject> written, CancellationToken cancellationToken)
    {
        var assets = new List<LayoutAsset>();

        foreach (var picture in pictures.Where(picture => picture.Existing is null))
        {
            var key = picture.Name[AssetPrefix.Length..];

            using var content = new MemoryStream(picture.Bytes, writable: false);

            var stored = await _storage.WriteAsync(
                    StorageBucket.Files,
                    $"files/{layout.UserId:N}/{layout.Id:N}/assets/{key}",
                    content,
                    picture.MediaType,
                    cancellationToken)
                .ConfigureAwait(false);

            written.Add(stored);
            assets.Add(new LayoutAsset
            {
                Key = key,
                Object = UploadFileHandler.ToFileObject(stored),
                MediaType = picture.MediaType,
                Sensitive = picture.Sensitive,
            });
        }

        return assets;
    }

    /// <summary>
    /// Draws the layout, checks the PDF against the size limit and the quota
    /// (with <paramref name="alsoStoring"/> bytes of pictures), and stores it.
    /// </summary>
    private async Task<FileObject> RenderAndStoreAsync(
        StoredFile file,
        string layoutJson,
        Func<string, byte[]?> images,
        string objectName,
        long maxBytes,
        long alsoStoring,
        CancellationToken cancellationToken)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-layout-{Guid.NewGuid():N}.pdf");

        try
        {
            await using (var output = new FileStream(
                             scratch, FileMode.CreateNew, System.IO.FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await _layouts.RenderAsync(layoutJson, images, output, cancellationToken).ConfigureAwait(false);
            }

            var length = new FileInfo(scratch).Length;

            FileIntake.EnsureWithin(length, maxBytes);
            await _intake.EnsureQuotaAsync(file.UserId, length + alsoStoring, cancellationToken).ConfigureAwait(false);

            await using var pdf = File.OpenRead(scratch);

            var stored = await _storage.WriteAsync(
                    StorageBucket.Files,
                    $"files/{file.UserId:N}/{file.Id:N}/{objectName}",
                    pdf,
                    "application/pdf",
                    cancellationToken)
                .ConfigureAwait(false);

            return UploadFileHandler.ToFileObject(stored);
        }
        finally
        {
            try
            {
                File.Delete(scratch);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "A layout's working copy could not be deleted.");
            }
        }
    }

    /// <summary>Records a new file and sends it for conversion: page count and previews, as for an upload.</summary>
    private async Task AddAsync(StoredFile file, List<StoredObject> written, CancellationToken cancellationToken)
    {
        try
        {
            await _files.AddAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync([ToStored(file.Original), .. written], file.Id).ConfigureAwait(false);
            throw;
        }

        await _events.PublishAsync(new FileUploaded(file.Id, _correlation.CorrelationId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<byte[]> ReadAllAsync(FileObject stored, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        await _storage.ReadAsync(ToStored(stored), StorageBucket.Files, buffer, cancellationToken).ConfigureAwait(false);

        return buffer.ToArray();
    }

    /// <summary>
    /// Deletes objects nothing will point at. A failure is logged, not thrown:
    /// the user's request has already succeeded or failed on its own merits.
    /// </summary>
    private async Task DeleteQuietlyAsync(IEnumerable<StoredObject> objects, Guid fileId)
    {
        foreach (var stored in objects)
        {
            try
            {
                await _storage.DeleteAsync(stored, StorageBucket.Files, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "An object of file {FileId} could not be deleted.", fileId);
            }
        }
    }

    private static StoredObject ToStored(FileObject stored) => new(
        stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId);
}
