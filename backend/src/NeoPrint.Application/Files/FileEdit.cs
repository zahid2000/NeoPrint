using System.Text;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Files;

// ---------------------------------------------------------------------------
// Editing (plan section 2.2, "Redaktə (MVP)"; plan section 6, POST files/{id}/edit)
// ---------------------------------------------------------------------------

/// <summary>One page of the result: which page of the current PDF, turned how far.</summary>
/// <param name="Page">One-based page of the current PDF.</param>
/// <param name="Rotate">Clockwise: 0, 90, 180 or 270.</param>
public sealed record PageEdit(int Page, int Rotate = 0);

/// <summary>A crop, as fractions of the image: 0,0 is the top left, 1,1 the bottom right.</summary>
public sealed record ImageCrop(double X, double Y, double Width, double Height);

/// <summary>What is done to an image, applied in this order: crop, rotate, grey, light.</summary>
/// <param name="Rotate">Clockwise: 0, 90, 180 or 270.</param>
/// <param name="Brightness">-100 to 100; 0 leaves it.</param>
/// <param name="Contrast">-100 to 100; 0 leaves it.</param>
public sealed record ImageEdit(int Rotate = 0, ImageCrop? Crop = null, bool Grayscale = false, int Brightness = 0, int Contrast = 0);

/// <summary>
/// One edit. Exactly one of the three is given, and which one decides what
/// kind of edit it is.
/// </summary>
/// <param name="Pages">
/// The pages of the result, in order: a page left out is deleted, a changed
/// order reorders, a rotation turns it. PDF and Office files (after
/// conversion).
/// </param>
/// <param name="Image">Image files.</param>
/// <param name="Text">The new content of a TXT file.</param>
public sealed record FileEditInput(IReadOnlyList<PageEdit>? Pages, ImageEdit? Image, string? Text);

/// <summary>A saved version, as the version list shows it.</summary>
public sealed record FileVersionItem(
    int Number, string Extension, long SizeBytes, DateTime CreatedAt, string? Note, bool IsCurrent);

/// <summary>A page, image or text operation that could not be carried out, in words the user can act on.</summary>
public sealed class FileEditException : Exception
{
    public FileEditException(string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
    }
}

/// <summary>
/// Carries out edits on file content (plan section 2.2): PDF page operations
/// with PDFsharp, image operations with SkiaSharp.
/// </summary>
public interface IFileEditor
{
    /// <summary>Writes a PDF made of the listed pages of <paramref name="pdf"/>.</summary>
    /// <exception cref="FileEditException">The PDF could not be read or written.</exception>
    Task EditPagesAsync(
        Stream pdf, IReadOnlyList<PageEdit> pages, Stream output, CancellationToken cancellationToken = default);

    /// <summary>Writes the edited image.</summary>
    /// <returns>The format written: the same as the input where it can be encoded, PNG otherwise.</returns>
    /// <exception cref="FileEditException">The image could not be read, or is too large.</exception>
    Task<string> EditImageAsync(
        Stream image, ImageEdit edit, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>Saves an edit as a new version of the file.</summary>
public sealed record EditFileCommand(Guid UserId, Guid FileId, FileEditInput Edit) : IRequest<UserFileResult>;

/// <summary>The saved versions, newest first.</summary>
public sealed record ListFileVersionsQuery(Guid UserId, Guid FileId) : IRequest<IReadOnlyList<FileVersionItem>>;

public sealed class EditFileCommandValidator : IValidator<EditFileCommand>
{
    /// <summary>A TXT file is text a person typed, not a data dump.</summary>
    public const int MaxTextLength = 1_000_000;

    private static readonly int[] Rotations = [0, 90, 180, 270];

    public IEnumerable<ValidationError> Validate(EditFileCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var edit = request.Edit;
        var given = (edit.Pages is null ? 0 : 1) + (edit.Image is null ? 0 : 1) + (edit.Text is null ? 0 : 1);

        if (given != 1)
        {
            yield return new ValidationError("edit", "Bir redaktə göndərin: səhifələr, şəkil və ya mətn.");
            yield break;
        }

        if (edit.Pages is { } pages)
        {
            if (pages.Count == 0)
            {
                yield return new ValidationError("pages", "Ən azı bir səhifə qalmalıdır.");
            }

            if (pages.Any(page => page.Page < 1))
            {
                yield return new ValidationError("pages", "Səhifə nömrəsi 1-dən başlayır.");
            }

            if (pages.Select(page => page.Page).Distinct().Count() != pages.Count)
            {
                yield return new ValidationError("pages", "Hər səhifə bir dəfə göstərilə bilər.");
            }

            if (pages.Any(page => !Rotations.Contains(page.Rotate)))
            {
                yield return new ValidationError("pages", "Fırlatma 0, 90, 180 və ya 270 dərəcə olmalıdır.");
            }
        }

        if (edit.Image is { } image)
        {
            if (!Rotations.Contains(image.Rotate))
            {
                yield return new ValidationError("image.rotate", "Fırlatma 0, 90, 180 və ya 270 dərəcə olmalıdır.");
            }

            if (image.Brightness is < -100 or > 100 || image.Contrast is < -100 or > 100)
            {
                yield return new ValidationError("image", "Parlaqlıq və kontrast -100 ilə 100 arasında olmalıdır.");
            }

            if (image.Crop is { } crop
                && (crop.X < 0 || crop.Y < 0 || crop.Width <= 0.01 || crop.Height <= 0.01
                    || crop.X + crop.Width > 1.0001 || crop.Y + crop.Height > 1.0001))
            {
                yield return new ValidationError("image.crop", "Kəsmə sahəsi şəklin daxilində olmalıdır.");
            }
        }

        if (edit.Text is { Length: > MaxTextLength })
        {
            yield return new ValidationError("text", "Mətn çox uzundur (ən çox 1 000 000 simvol).");
        }
    }
}

/// <inheritdoc cref="EditFileCommand" />
/// <remarks>
/// <para>
/// The original is never touched (plan section 2.2). The edit becomes the
/// newest version, the file goes back to Pending, and the conversion consumer
/// makes its PDF and counts its pages exactly as for an upload - so price,
/// preview and print follow the edit.
/// </para>
/// <para>
/// Page operations work on the current PDF, so they apply to Office files
/// after conversion too, and produce a PDF version. PS, PCL and XPS files are
/// not edited: only a page range is chosen, at print time.
/// </para>
/// </remarks>
public sealed class EditFileHandler :
    IRequestHandler<EditFileCommand, UserFileResult>,
    IRequestHandler<ListFileVersionsQuery, IReadOnlyList<FileVersionItem>>
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly IFileEditor _editor;
    private readonly ISettingsReader _settings;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<EditFileHandler> _logger;

    public EditFileHandler(
        IFileRepository files,
        IFileStorage storage,
        IFileEditor editor,
        ISettingsReader settings,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<EditFileHandler> logger)
    {
        _files = files;
        _storage = storage;
        _editor = editor;
        _settings = settings;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FileVersionItem>> Handle(
        ListFileVersionsQuery request, CancellationToken cancellationToken)
    {
        var file = await FileAccess.LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        var latest = file.LatestVersion?.Number;

        return [.. file.Versions
            .OrderByDescending(version => version.Number)
            .Select(version => new FileVersionItem(
                version.Number,
                version.Extension ?? file.Extension,
                version.Object.PlaintextLength,
                version.CreatedAt,
                version.Note,
                version.Number == latest))];
    }

    public async Task<UserFileResult> Handle(EditFileCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var file = await FileAccess.LoadOwnedAsync(_files, request.UserId, request.FileId, cancellationToken)
            .ConfigureAwait(false);

        var original = FileFormats.Find(file.Extension);

        if (original?.Group == FormatGroup.PageDescription)
        {
            throw Invalid("edit", "PS, PCL və XPS faylları redaktə olunmur — çapda səhifə aralığı seçə bilərsiniz.");
        }

        if (file.Status != FileStatus.Ready || file.Pdf is null)
        {
            throw Invalid("edit", "Fayl hələ hazırlanır. Hazır olandan sonra redaktə edin.");
        }

        var number = (file.LatestVersion?.Number ?? 0) + 1;
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-edit-{Guid.NewGuid():N}");

        try
        {
            string extension;
            string note;

            await using (var output = new FileStream(
                             scratch, FileMode.CreateNew, System.IO.FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                (extension, note) = await EditAsync(file, request.Edit, output, cancellationToken)
                    .ConfigureAwait(false);
            }

            var format = FileFormats.Find(extension)!;

            StoredObject stored;

            await using (var edited = File.OpenRead(scratch))
            {
                stored = await _storage.WriteAsync(
                        StorageBucket.Files,
                        $"files/{file.UserId:N}/{file.Id:N}/v{number}.{extension}",
                        edited,
                        format.MediaType,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var keep = await _settings.GetIntAsync(SettingKeys.FilesVersionsToKeep, 5, cancellationToken)
                .ConfigureAwait(false);

            var dropped = file.ApplyEdit(
                new FileObject(
                    stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
                extension,
                note,
                Math.Max(1, keep),
                ActorContext.User(request.UserId, "Fayl redaktə edildi"),
                _clock.UtcNow);

            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

            await DeleteDroppedAsync(file, dropped, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "File {FileId} was edited: version {Version} ({Extension}).", file.Id, number, extension);

            // The same path as an upload: convert, count, then Ready.
            await _events.PublishAsync(new FileUploaded(file.Id, _correlation.CorrelationId), cancellationToken)
                .ConfigureAwait(false);

            return UploadFileHandler.Map(file);
        }
        catch (FileEditException exception)
        {
            throw Invalid("edit", exception.Message);
        }
        finally
        {
            try
            {
                File.Delete(scratch);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "An edit's working copy could not be deleted.");
            }
        }
    }

    private async Task<(string Extension, string Note)> EditAsync(
        StoredFile file, FileEditInput edit, Stream output, CancellationToken cancellationToken)
    {
        if (edit.Pages is { } pages)
        {
            var outside = pages.FirstOrDefault(page => page.Page > file.PageCount);

            if (outside is not null)
            {
                throw Invalid("pages", $"Sənəddə {file.PageCount} səhifə var; {outside.Page}-ci səhifə yoxdur.");
            }

            await using var pdf = await _storage.OpenReadAsync(ToStored(file.Pdf!), StorageBucket.Files, cancellationToken)
                .ConfigureAwait(false);

            await _editor.EditPagesAsync(pdf, pages, output, cancellationToken).ConfigureAwait(false);

            return ("pdf", PageNote(file.PageCount, pages));
        }

        var current = FileFormats.Find(file.CurrentExtension);

        if (edit.Image is { } image)
        {
            if (current?.Group != FormatGroup.Image)
            {
                throw Invalid("image", "Şəkil redaktəsi yalnız şəkil faylları üçündür.");
            }

            await using var source = await _storage
                .OpenReadAsync(ToStored(file.CurrentContent), StorageBucket.Files, cancellationToken)
                .ConfigureAwait(false);

            var extension = await _editor.EditImageAsync(source, image, output, cancellationToken)
                .ConfigureAwait(false);

            return (extension, "Şəkil redaktə edildi");
        }

        if (current?.Extension != "txt")
        {
            throw Invalid("text", "Mətn redaktəsi yalnız TXT faylları üçündür.");
        }

        // UTF-8 without a byte order mark, as the upload's text detection expects.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(edit.Text!);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

        return ("txt", "Mətn redaktə edildi");
    }

    /// <summary>"3 səhifədən 2-si saxlanıldı, sıra dəyişdi, 1 səhifə fırlandı".</summary>
    private static string PageNote(int before, IReadOnlyList<PageEdit> pages)
    {
        var parts = new List<string>();

        if (pages.Count != before)
        {
            parts.Add($"{before} səhifədən {pages.Count}-i saxlanıldı");
        }

        if (!pages.Select(page => page.Page).SequenceEqual(pages.Select(page => page.Page).Order()))
        {
            parts.Add("sıra dəyişdi");
        }

        var rotated = pages.Count(page => page.Rotate != 0);

        if (rotated > 0)
        {
            parts.Add($"{rotated} səhifə fırlandı");
        }

        return parts.Count == 0 ? "Səhifələr dəyişmədi" : string.Join(", ", parts);
    }

    /// <summary>
    /// Deletes the objects of versions trimmed to stay within
    /// <c>Files.VersionsToKeep</c>.
    /// </summary>
    /// <remarks>
    /// Not the one the current PDF still points at: previews and prints read
    /// it until the new version's conversion replaces it, and the conversion
    /// deletes it then.
    /// </remarks>
    private async Task DeleteDroppedAsync(
        StoredFile file, IReadOnlyList<FileVersion> dropped, CancellationToken cancellationToken)
    {
        foreach (var version in dropped.Where(version => version.Object.Key != file.Pdf?.Key))
        {
            try
            {
                await _storage.DeleteAsync(ToStored(version.Object), StorageBucket.Files, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception, "Version {Version} of file {FileId} could not be deleted.", version.Number, file.Id);
            }
        }
    }

    private static StoredObject ToStored(FileObject stored) => new(
        stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId);

    private static ValidationException Invalid(string field, string message) => new([new ValidationError(field, message)]);
}
