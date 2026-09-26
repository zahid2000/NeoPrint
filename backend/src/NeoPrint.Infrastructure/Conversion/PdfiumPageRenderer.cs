using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Pdfium;

using SkiaSharp;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// Renders PDF pages with PDFium and encodes them as PNG with SkiaSharp.
/// </summary>
/// <remarks>
/// <para>
/// Only PDFs the conversion pipeline produced or accepted reach this: a file
/// is rendered only once it is Ready, which means PDFium has already opened
/// and counted it. Rendering runs in this process, as the page count does;
/// the full isolation of document handling is step 8 (plan section 2.2,
/// <c>docs/DECISIONS.md</c>).
/// </para>
/// <para>
/// Rendering is CPU- and memory-heavy, so only a few run at once. A preview
/// request beyond that waits rather than taking the process down with it.
/// </para>
/// </remarks>
public sealed class PdfiumPageRenderer : IPageRenderer
{
    private static readonly SemaphoreSlim Gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    private readonly ILogger<PdfiumPageRenderer> _logger;

    public PdfiumPageRenderer(ILogger<PdfiumPageRenderer> logger) => _logger = logger;

    public bool IsAvailable => PdfiumLibrary.IsAvailable;

    public async Task<byte[]> RenderPngAsync(
        Stream pdf, int pageIndex, int width, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // PDFium reads from a file; the decrypted copy lives only as long as
        // this call and is deleted even when rendering fails.
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-render-{Guid.NewGuid():N}.pdf");

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (var file = new FileStream(
                             scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await pdf.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            using var document = await PdfDocument.LoadAsync(scratch, cancellationToken).ConfigureAwait(false);

            if (pageIndex < 0 || pageIndex >= document.PageCount)
            {
                throw new PageRenderException($"Sənəddə {document.PageCount} səhifə var.");
            }

            var (pixels, renderedWidth, height, stride) = document.RenderPage(pageIndex, width);

            return EncodePng(pixels, renderedWidth, height, stride);
        }
        catch (PdfiumException exception)
        {
            _logger.LogWarning(exception, "Could not render page {Page}.", pageIndex + 1);
            throw new PageRenderException(exception.UserMessage, exception);
        }
        finally
        {
            Gate.Release();
            TryDelete(scratch);
        }
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height, int stride)
    {
        // PDFium writes BGRA, opaque (the page is filled white first).
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

        using var image = SKImage.FromPixelCopy(info, pixels, stride)
                          ?? throw new PageRenderException("Səhifənin şəkli yaradıla bilmədi.");
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new PageRenderException("Səhifənin şəkli yaradıla bilmədi.");

        return data.ToArray();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "A decrypted render copy could not be deleted.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "A decrypted render copy could not be deleted.");
        }
    }
}
