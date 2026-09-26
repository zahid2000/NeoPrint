using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;
using NeoPrint.Pdfium;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// Turns an uploaded file into the PDF everything downstream uses
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// <para>
/// One PDF per file is what makes the rest of the system simple: the page
/// count that the price is computed from, the preview that matches what will
/// be printed, page ranges, pages-per-sheet and real-size printing all need a
/// rendered document. A file that is already a PDF skips the conversion but
/// still goes through here, so nothing downstream has to special-case it.
/// </para>
/// <para>
/// The page limit is enforced here rather than at upload: the page count is
/// not knowable until the document has been rendered, and a 2 MB spreadsheet
/// can be four thousand pages.
/// </para>
/// </remarks>
public sealed class DocumentConversionService
{
    private readonly IFileRepository _files;
    private readonly IFileStorage _storage;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<ConversionRoute, IDocumentConverter> _converters;
    private readonly ILogger<DocumentConversionService> _logger;

    public DocumentConversionService(
        IFileRepository files,
        IFileStorage storage,
        ISettingsReader settings,
        IClock clock,
        IEnumerable<IDocumentConverter> converters,
        ILogger<DocumentConversionService> logger)
    {
        ArgumentNullException.ThrowIfNull(converters);

        _files = files;
        _storage = storage;
        _settings = settings;
        _clock = clock;
        _converters = converters.ToDictionary(converter => converter.Route);
        _logger = logger;
    }

    /// <summary>
    /// Converts a file and records the result.
    /// </summary>
    /// <remarks>
    /// Idempotent: a file already <see cref="FileStatus.Ready"/> is left
    /// alone, so a redelivered message does not convert it a second time
    /// (<c>docs/IDEMPOTENCY.md</c>).
    /// </remarks>
    public async Task ConvertAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false);

        if (file is null || file.DeletedAt is not null)
        {
            _logger.LogInformation(
                "File {FileId} is gone; nothing to convert.", fileId);
            return;
        }

        if (file.Status == FileStatus.Ready)
        {
            return;
        }

        var actor = ActorContext.System($"Konvertasiya: {file.CurrentExtension}");

        file.BeginConversion(actor, _clock.UtcNow);
        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        // A directory per conversion, deleted whatever happens: the tool has
        // nowhere else to write, and nothing of one file's content survives
        // into the next conversion.
        var workspace = Path.Combine(
            Path.GetTempPath(), $"neoprint-convert-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        try
        {
            await RunAsync(file, workspace, actor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Converting file {FileId} failed unexpectedly.", file.Id);

            file.Fail("Faylı emal etmək mümkün olmadı. Bir az sonra yenidən cəhd edin.", actor, _clock.UtcNow);
            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    private async Task RunAsync(
        StoredFile file, string workspace, ActorContext actor, CancellationToken cancellationToken)
    {
        // The newest version when the file was edited, the original otherwise
        // (plan section 2.2: the original is never changed).
        var extension = file.CurrentExtension;
        var source = file.CurrentContent;
        var format = FileFormats.Find(extension);

        if (format is null)
        {
            file.Fail($"'{extension}' formatı artıq dəstəklənmir.", actor, _clock.UtcNow);
            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
            return;
        }

        var inputPath = Path.Combine(workspace, $"input.{extension}");

        await using (var input = File.Create(inputPath))
        {
            await _storage.ReadAsync(ToStoredObject(source), StorageBucket.Files, input, cancellationToken)
                .ConfigureAwait(false);
        }

        string pdfPath;

        if (format.Route == ConversionRoute.None)
        {
            // Already a PDF. Still read and counted below, which is also how a
            // corrupt or encrypted PDF is caught before it reaches a printer.
            pdfPath = inputPath;
        }
        else
        {
            if (!_converters.TryGetValue(format.Route, out var converter))
            {
                file.Fail("Bu format üçün çevirici quraşdırılmayıb.", actor, _clock.UtcNow);
                await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
                return;
            }

            var limits = SandboxLimits.Default(workspace);
            var result = await converter.ConvertAsync(inputPath, limits, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                file.Fail(result.UserMessage ?? "Faylı çevirmək mümkün olmadı.", actor, _clock.UtcNow);
                await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
                return;
            }

            pdfPath = result.PdfPath!;
        }

        int pageCount;

        try
        {
            using var document = await PdfDocument.LoadAsync(pdfPath, cancellationToken)
                .ConfigureAwait(false);

            pageCount = document.PageCount;
        }
        catch (PdfiumException exception)
        {
            _logger.LogWarning(
                exception, "PDFium refused the PDF produced for file {FileId}.", file.Id);

            file.Fail(exception.UserMessage, actor, _clock.UtcNow);
            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
            return;
        }

        var maxPages = await _settings
            .GetIntAsync(SettingKeys.FilesDefaultMaxPages, 200, cancellationToken)
            .ConfigureAwait(false);

        if (pageCount > maxPages)
        {
            // Enforced here because the count is not knowable at upload: a
            // 2 MB spreadsheet can be four thousand pages.
            file.Fail(
                $"Sənəd {pageCount} səhifədir; icazə verilən maksimum {maxPages} səhifədir.",
                actor,
                _clock.UtcNow);

            await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
            return;
        }

        FileObject pdf;

        var previous = file.Pdf;

        if (format.Route == ConversionRoute.None)
        {
            // The source is the PDF: pointing at the same object rather than
            // storing the bytes twice.
            pdf = source;
        }
        else
        {
            await using var converted = File.OpenRead(pdfPath);

            var stored = await _storage.WriteAsync(
                    StorageBucket.Files,
                    file.LatestVersion is { } version
                        ? $"files/{file.UserId:N}/{file.Id:N}/print-v{version.Number}.pdf"
                        : $"files/{file.UserId:N}/{file.Id:N}/print.pdf",
                    converted,
                    "application/pdf",
                    cancellationToken)
                .ConfigureAwait(false);

            pdf = new FileObject(
                stored.ProviderId,
                stored.Key,
                stored.Sha256,
                stored.EncryptedLength,
                stored.PlaintextLength,
                stored.KekId);
        }

        file.Complete(pdf, pageCount, actor, _clock.UtcNow);

        await _files.UpdateAsync(file, cancellationToken).ConfigureAwait(false);

        await DeleteIfUnusedAsync(file, previous, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "File {FileId} converted from {Extension}: {PageCount} pages.",
            file.Id,
            extension,
            pageCount);
    }

    /// <summary>
    /// Deletes the PDF an edit replaced, unless something still uses it.
    /// </summary>
    /// <remarks>
    /// The PDF rendered from a Word original is its own object; once an edit
    /// produced a new PDF, nothing refers to the old one and it would sit in
    /// storage, encrypted but unaccounted for, until the file was deleted - and
    /// not even then, since the file no longer lists it.
    /// </remarks>
    private async Task DeleteIfUnusedAsync(StoredFile file, FileObject? previous, CancellationToken cancellationToken)
    {
        if (previous is null
            || previous.Key == file.Pdf?.Key
            || previous.Key == file.Original.Key
            || file.Versions.Any(version => version.Object.Key == previous.Key))
        {
            return;
        }

        try
        {
            await _storage.DeleteAsync(ToStoredObject(previous), StorageBucket.Files, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The new PDF is in place; a leftover object is not worth failing
            // the conversion over.
            _logger.LogWarning(exception, "The replaced PDF of file {FileId} could not be deleted.", file.Id);
        }
    }

    private static StoredObject ToStoredObject(FileObject stored) => new(
        stored.ProviderId,
        stored.Key,
        stored.Sha256,
        stored.EncryptedLength,
        stored.PlaintextLength,
        stored.KekId);

    private void Cleanup(string workspace)
    {
        try
        {
            Directory.Delete(workspace, recursive: true);
        }
        catch (IOException exception)
        {
            // A converter may still hold a handle for a moment after being
            // killed. The directory is under the system temp path, so it is
            // cleaned up eventually; failing the conversion over it would be
            // worse.
            _logger.LogWarning(exception, "Could not delete the conversion workspace {Workspace}.", workspace);
        }
    }
}
