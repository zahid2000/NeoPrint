using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Application.Localization;
using NeoPrint.Documents;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// A registered user's files (plan sections 2.2, 6).
/// </summary>
/// <remarks>
/// Every endpoint here is scoped to the token's own user; a file belonging to
/// somebody else answers 404 rather than 403, because a 403 would confirm the
/// id exists.
/// </remarks>
[ApiController]
[Route("api/files")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class FilesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;

    public FilesController(
        ISender sender,
        ICurrentUser currentUser,
        IFileRepository files,
        IFileStorage storage)
    {
        _sender = sender;
        _currentUser = currentUser;
        _files = files;
        _storage = storage;
    }

    private Guid UserId => _currentUser.UserId
        ?? throw new UnauthorizedAccessException("A user token is required.");

    /// <summary>
    /// Uploads a file.
    /// </summary>
    /// <remarks>
    /// The body is streamed straight into storage rather than buffered: a
    /// 50 MB upload must not sit in memory, and the encryptor works in chunks
    /// for the same reason. <c>DisableFormValueModelBinding</c> is not needed
    /// because the file section is read from the multipart reader by hand.
    /// </remarks>
    [HttpPost]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserFileResult>> Upload(
        IFormFile file,
        [FromForm] Guid? kioskId,
        [FromForm] string? source,
        [FromForm] bool isSensitive,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ValidationProblem(new UserMessage("files.noFileSent"));
        }

        await using var content = file.OpenReadStream();

        var result = await _sender.Send(
            new UploadFileCommand(
                UserId,
                file.FileName,
                content,
                file.Length,
                kioskId,
                ParseSource(source),
                isSensitive),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>
    /// Saves a scan as a finished PDF (plan section 2.11; docs/DECISIONS.md
    /// 2026-10-05). Multipart: the pictures as <c>images</c> parts, each
    /// part's file name being its name in <c>layout</c>; the layout JSON;
    /// <c>documentType</c> (a preset, or <c>Custom</c> with
    /// <c>physicalWidthMm</c> / <c>physicalHeightMm</c>); <c>isSensitive</c>;
    /// <c>fileName</c>. Requires the current scan notice to be accepted.
    /// </summary>
    [HttpPost("scan")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserFileResult>> Scan(
        [FromForm] List<IFormFile> images,
        [FromForm] string? layout,
        [FromForm] string? documentType,
        [FromForm] double? physicalWidthMm,
        [FromForm] double? physicalHeightMm,
        [FromForm] bool isSensitive,
        [FromForm] string? fileName,
        CancellationToken cancellationToken)
    {
        var pictures = new List<ScanPicture>(images?.Count ?? 0);

        foreach (var image in images ?? [])
        {
            await using var stream = image.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            pictures.Add(new ScanPicture(image.FileName, buffer.ToArray()));
        }

        var result = await _sender.Send(
            new CreateScanCommand(
                UserId, pictures, layout ?? string.Empty, documentType, physicalWidthMm, physicalHeightMm, isSensitive, fileName),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>
    /// Saves a new page layout (plan section 2.11). Pictures are named
    /// <c>file:{id}</c> (an image file) or <c>file:{id}:{page}</c> (a page of
    /// any file); the layout keeps its own copy of each.
    /// </summary>
    [HttpPost("layout")]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status201Created)]
    public async Task<ActionResult<UserFileResult>> CreateLayout(
        [FromBody] LayoutRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new CreateLayoutCommand(UserId, request?.Layout.GetRawText() ?? string.Empty, request?.FileName),
            cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    /// <summary>
    /// Saves a changed layout as a new version. Pictures the layout already
    /// holds keep their <c>asset:{key}</c> names; new ones are named as for
    /// a new layout.
    /// </summary>
    [HttpPut("{id:guid}/layout")]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserFileResult>> UpdateLayout(
        Guid id, [FromBody] LayoutRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdateLayoutCommand(UserId, id, request?.Layout.GetRawText() ?? string.Empty), cancellationToken));

    /// <summary>A layout file's layout and its pictures, for "Tərtibatı redaktə et".</summary>
    [HttpGet("{id:guid}/layout")]
    [ProducesResponseType(typeof(LayoutResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LayoutResponse>> GetLayout(Guid id, CancellationToken cancellationToken)
    {
        var view = await _sender.Send(new GetLayoutQuery(UserId, id), cancellationToken);

        using var document = JsonDocument.Parse(view.LayoutJson);

        return Ok(new LayoutResponse(view.FileId, document.RootElement.Clone(), view.Assets));
    }

    /// <summary>One of a layout's pictures, decrypted, for the editor to draw.</summary>
    /// <param name="id">The layout file.</param>
    /// <param name="key">The picture's key: its name without <c>asset:</c>.</param>
    [HttpGet("{id:guid}/layout/assets/{key}")]
    public async Task<IActionResult> LayoutAsset(Guid id, string key, CancellationToken cancellationToken)
    {
        var file = await LoadOwnedAsync(id, cancellationToken);
        var asset = file.LayoutAssets.FirstOrDefault(candidate => candidate.Key == key)
                    ?? throw new NeoPrint.Domain.Common.NotFoundException("asset");

        var stream = await _storage.OpenReadAsync(ToStoredObject(asset.Object), StorageBucket.Files, cancellationToken);

        Response.Headers.CacheControl = "private, max-age=86400";

        return File(stream, asset.MediaType);
    }

    /// <summary>One page of the user's files, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FileListResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<FileListResult>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListFilesQuery(UserId, page, pageSize), cancellationToken));

    /// <summary>One file's metadata.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserFileResult>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetFileQuery(UserId, id), cancellationToken));

    /// <summary>
    /// The file itself, decrypted.
    /// </summary>
    /// <remarks>
    /// Served through the API rather than as a pre-signed link to the object
    /// store: what is in the store is ciphertext, and a link to it would hand
    /// out bytes nobody can use and bypass the ownership check while doing it
    /// (plan section 2.13).
    /// </remarks>
    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> Content(Guid id, CancellationToken cancellationToken)
    {
        var file = await LoadOwnedAsync(id, cancellationToken);

        var stream = await _storage.OpenReadAsync(
            ToStoredObject(file.Original), StorageBucket.Files, cancellationToken);

        // enableRangeProcessing: a preview scrubbing through a large PDF asks
        // for ranges, and the stream is seekable.
        return File(stream, file.MediaType, file.FileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// The converted PDF: what previews, page counts and printing all use.
    /// </summary>
    [HttpGet("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id, CancellationToken cancellationToken)
    {
        var file = await LoadOwnedAsync(id, cancellationToken);

        if (file.Pdf is null)
        {
            return ValidationProblem(
                file.Status == FileStatus.Failed
                    ? file.FailureReason is { } reason ? UserMessage.Decode(reason) : new UserMessage("files.conversionFailed")
                    : new UserMessage("files.stillConverting"));
        }

        var stream = await _storage.OpenReadAsync(
            ToStoredObject(file.Pdf), StorageBucket.Files, cancellationToken);

        return File(
            stream,
            "application/pdf",
            Path.ChangeExtension(file.FileName, ".pdf"),
            enableRangeProcessing: true);
    }

    /// <summary>
    /// One page as a PNG, for previews (plan section 6).
    /// </summary>
    /// <param name="id">The file.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="width">Requested width in pixels; snapped to 320, 640, 960, 1280 or 1600.</param>
    /// <remarks>
    /// Private caching only: the image is the user's document. The ETag follows
    /// the PDF's content, so an unchanged page is answered with 304 and never
    /// rendered twice for the same client.
    /// </remarks>
    [HttpGet("{id:guid}/pages/{page:int}.png")]
    public async Task<IActionResult> PageImage(
        Guid id, int page, [FromQuery] int? width, CancellationToken cancellationToken)
    {
        var image = await _sender.Send(
            new GetFilePageImageQuery(UserId, id, page, width, Request.Headers.IfNoneMatch.ToString()),
            cancellationToken);

        Response.Headers.ETag = image.ETag;
        Response.Headers.CacheControl = "private, max-age=86400";

        return image.NotModified
            ? StatusCode(StatusCodes.Status304NotModified)
            : File(image.Png!, "image/png");
    }

    /// <summary>
    /// Saves an edit as a new version (plan section 2.2): pages, an image or
    /// text. The original is kept; the file is converted again, so it comes
    /// back as Pending and turns Ready with the new page count.
    /// </summary>
    [HttpPost("{id:guid}/edit")]
    public async Task<ActionResult<UserFileResult>> Edit(
        Guid id, [FromBody] FileEditInput edit, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new EditFileCommand(UserId, id, edit), cancellationToken));

    /// <summary>The saved versions, newest first.</summary>
    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<FileVersionItem>>> Versions(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListFileVersionsQuery(UserId, id), cancellationToken));

    /// <summary>One saved version's content, decrypted.</summary>
    [HttpGet("{id:guid}/versions/{number:int}/content")]
    public async Task<IActionResult> VersionContent(Guid id, int number, CancellationToken cancellationToken)
    {
        var file = await LoadOwnedAsync(id, cancellationToken);

        var version = file.Versions.FirstOrDefault(v => v.Number == number)
                      ?? throw new NeoPrint.Domain.Common.NotFoundException("version");

        var extension = version.Extension ?? file.Extension;

        var stream = await _storage.OpenReadAsync(
            ToStoredObject(version.Object), StorageBucket.Files, cancellationToken);

        return File(
            stream,
            FileFormats.Find(extension)?.MediaType ?? "application/octet-stream",
            $"{Path.GetFileNameWithoutExtension(file.FileName)} (v{number}).{extension}",
            enableRangeProcessing: true);
    }

    /// <summary>Changes when the file deletes itself (plan section 2.13).</summary>
    [HttpPut("{id:guid}/auto-delete")]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserFileResult>> SetAutoDelete(
        Guid id, [FromBody] SetAutoDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AutoDeleteRule>(request?.Rule, ignoreCase: true, out var rule))
        {
            return ValidationProblem(new UserMessage("files.autoDeleteUnknown"));
        }

        return Ok(await _sender.Send(new SetAutoDeleteCommand(UserId, id, rule), cancellationToken));
    }

    /// <summary>Deletes the file and every object stored for it.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteFileCommand(UserId, id), cancellationToken);

        return NoContent();
    }

    /// <summary>The formats this installation currently accepts.</summary>
    /// <remarks>
    /// So the mobile file picker can filter, rather than letting somebody
    /// choose a file that the upload will then refuse - which is why it lists
    /// only the formats switched on in <c>Files.AllowedFormats</c>, not the
    /// whole catalogue.
    /// </remarks>
    [HttpGet("formats")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<string>>> Formats(CancellationToken cancellationToken)
    {
        var formats = await _sender.Send(new GetFileFormatsQuery(), cancellationToken);

        return Ok(formats.Formats.Where(format => format.Enabled).Select(format => format.Extension).ToList());
    }

    private async Task<StoredFile> LoadOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await _files.FindByIdAsync(id, cancellationToken);

        if (file is null || file.UserId != UserId || file.DeletedAt is not null)
        {
            throw new NeoPrint.Domain.Common.NotFoundException("file");
        }

        return file;
    }

    private static StoredObject ToStoredObject(FileObject stored) => new(
        stored.ProviderId,
        stored.Key,
        stored.Sha256,
        stored.EncryptedLength,
        stored.PlaintextLength,
        stored.KekId);

    private static FileSource ParseSource(string? source)
        => Enum.TryParse<FileSource>(source, ignoreCase: true, out var parsed)
            ? parsed
            : FileSource.Uploaded;

    private ActionResult ValidationProblem(UserMessage message)
        => base.ValidationProblem(new ValidationProblemDetails(
            new Dictionary<string, string[]> { ["file"] = [Messages.Write(message)] })
        {
            Title = Messages.Write(new UserMessage("problem.validation")),
        });
}

/// <param name="Rule">One of Never, AfterPrint, AfterOneDay, AfterSevenDays, AfterThirtyDays.</param>
public sealed record SetAutoDeleteRequest(string? Rule);

/// <param name="Layout">The layout (<c>NeoPrint.Layout</c> JSON).</param>
/// <param name="FileName">The name the new layout file is saved under.</param>
public sealed record LayoutRequest(JsonElement Layout, string? FileName);

/// <param name="Layout">Pictures named <c>asset:{key}</c>, as <see cref="FilesController.LayoutAsset"/> serves them.</param>
public sealed record LayoutResponse(Guid FileId, JsonElement Layout, IReadOnlyList<LayoutAssetView> Assets);
