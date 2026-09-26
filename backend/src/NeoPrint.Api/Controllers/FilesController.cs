using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
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
            return ValidationProblem("Fayl göndərilməyib.");
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
                    ? file.FailureReason ?? "Fayl çevrilə bilmədi."
                    : "Fayl hələ çevrilir. Bir az sonra yenidən cəhd edin.");
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

    /// <summary>Changes when the file deletes itself (plan section 2.13).</summary>
    [HttpPut("{id:guid}/auto-delete")]
    [ProducesResponseType(typeof(UserFileResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserFileResult>> SetAutoDelete(
        Guid id, [FromBody] SetAutoDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<AutoDeleteRule>(request?.Rule, ignoreCase: true, out var rule))
        {
            return ValidationProblem("Avtomatik silinmə qaydası tanınmadı.");
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
    /// choose a file that the upload will then refuse.
    /// </remarks>
    [HttpGet("formats")]
    [AllowAnonymous]
    public ActionResult<IReadOnlyList<string>> Formats() => Ok(FileFormats.Extensions);

    private async Task<StoredFile> LoadOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var file = await _files.FindByIdAsync(id, cancellationToken);

        if (file is null || file.UserId != UserId || file.DeletedAt is not null)
        {
            throw new NeoPrint.Domain.Common.NotFoundException("Fayl");
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

    private ActionResult ValidationProblem(string message)
        => base.ValidationProblem(new ValidationProblemDetails(
            new Dictionary<string, string[]> { ["file"] = [message] }));
}

/// <param name="Rule">One of Never, AfterPrint, AfterOneDay, AfterSevenDays, AfterThirtyDays.</param>
public sealed record SetAutoDeleteRequest(string? Rule);
