using Microsoft.Extensions.Logging;

using NeoPrint.Application.Files;

using PdfSharp.Pdf.IO;

using SkiaSharp;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// PDF page operations with PDFsharp and image operations with SkiaSharp
/// (plan sections 2.0, 2.2).
/// </summary>
/// <remarks>
/// <para>
/// Both work on content the conversion pipeline has already accepted: a page
/// edit reads the file's current PDF, which PDFium has opened and counted; an
/// image edit reads an image that passed the upload's signature check. Like
/// the page count, they run in this process; isolating document handling
/// completely is step 8 (<c>docs/DECISIONS.md</c>).
/// </para>
/// <para>
/// Images are held to the same pixel limit as conversion, checked from the
/// header before anything is decoded.
/// </para>
/// </remarks>
public sealed class FileEditor : IFileEditor
{
    private readonly ILogger<FileEditor> _logger;

    public FileEditor(ILogger<FileEditor> logger) => _logger = logger;

    // -----------------------------------------------------------------------
    // PDF pages
    // -----------------------------------------------------------------------

    public async Task EditPagesAsync(
        Stream pdf, IReadOnlyList<PageEdit> pages, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(output);

        // PDFsharp seeks around the file; the decrypted stream is copied to a
        // local file first, deleted when done.
        var scratch = Path.Combine(Path.GetTempPath(), $"neoprint-pages-{Guid.NewGuid():N}.pdf");

        try
        {
            await using (var copy = File.Create(scratch))
            {
                await pdf.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var input = PdfReader.Open(scratch, PdfDocumentOpenMode.Import);
            using var result = new PdfSharp.Pdf.PdfDocument();

            foreach (var edit in pages)
            {
                if (edit.Page < 1 || edit.Page > input.PageCount)
                {
                    throw new FileEditException($"Sənəddə {input.PageCount} səhifə var; {edit.Page}-ci səhifə yoxdur.");
                }

                var page = result.AddPage(input.Pages[edit.Page - 1]);

                // Added to whatever rotation the page already had, so a page
                // that was stored sideways turns from where the user saw it.
                page.Rotate = (((page.Rotate + edit.Rotate) % 360) + 360) % 360;
            }

            result.Save(output, closeStream: false);
        }
        catch (FileEditException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "A page edit could not be carried out.");
            throw new FileEditException(
                "Sənədin səhifələrini dəyişmək mümkün olmadı. Sənəd şifrələnib və ya zədələnib ola bilər.", exception);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    // -----------------------------------------------------------------------
    // Images
    // -----------------------------------------------------------------------

    public async Task<string> EditImageAsync(
        Stream image, ImageEdit edit, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new MemoryStream();
        await image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        using var codec = SKCodec.Create(buffer)
                          ?? throw new FileEditException("Şəkli oxumaq mümkün olmadı. Fayl zədələnib ola bilər.");

        if ((long)codec.Info.Width * codec.Info.Height > ImageConverter.MaxPixels)
        {
            throw new FileEditException("Şəkil həddindən artıq böyükdür.");
        }

        var (format, extension) = OutputFormat(codec.EncodedFormat);

        using var decoded = SKBitmap.Decode(codec)
                            ?? throw new FileEditException("Şəkli açmaq mümkün olmadı.");

        cancellationToken.ThrowIfCancellationRequested();

        using var cropped = Crop(decoded, edit.Crop);
        using var rotated = Rotate(cropped ?? decoded, edit.Rotate);
        using var finished = Adjust(rotated ?? cropped ?? decoded, edit);

        using var encodedImage = SKImage.FromBitmap(finished ?? rotated ?? cropped ?? decoded);
        using var data = encodedImage.Encode(format, format == SKEncodedImageFormat.Jpeg ? 92 : 100)
                         ?? throw new FileEditException("Redaktə olunmuş şəkli yazmaq mümkün olmadı.");

        await output.WriteAsync(data.ToArray(), cancellationToken).ConfigureAwait(false);

        return extension;
    }

    /// <summary>
    /// The input's own format where Skia can write it, PNG otherwise - it
    /// loses nothing, and GIF and BMP cannot be encoded.
    /// </summary>
    private static (SKEncodedImageFormat Format, string Extension) OutputFormat(SKEncodedImageFormat input)
        => input switch
        {
            SKEncodedImageFormat.Jpeg => (SKEncodedImageFormat.Jpeg, "jpg"),
            SKEncodedImageFormat.Webp => (SKEncodedImageFormat.Webp, "webp"),
            _ => (SKEncodedImageFormat.Png, "png"),
        };

    private static SKBitmap? Crop(SKBitmap source, ImageCrop? crop)
    {
        if (crop is null)
        {
            return null;
        }

        var rect = SKRectI.Create(
            (int)Math.Round(crop.X * source.Width),
            (int)Math.Round(crop.Y * source.Height),
            Math.Max(1, (int)Math.Round(crop.Width * source.Width)),
            Math.Max(1, (int)Math.Round(crop.Height * source.Height)));

        rect.Intersect(SKRectI.Create(0, 0, source.Width, source.Height));

        var result = new SKBitmap(rect.Width, rect.Height, source.ColorType, source.AlphaType);

        if (!source.ExtractSubset(result, rect))
        {
            result.Dispose();
            throw new FileEditException("Şəkli kəsmək mümkün olmadı.");
        }

        // ExtractSubset shares pixels with the source; a copy stands alone.
        var copy = result.Copy();
        result.Dispose();
        return copy;
    }

    private static SKBitmap? Rotate(SKBitmap source, int degrees)
    {
        var turn = ((degrees % 360) + 360) % 360;

        if (turn == 0)
        {
            return null;
        }

        var sideways = turn is 90 or 270;
        var result = new SKBitmap(
            sideways ? source.Height : source.Width,
            sideways ? source.Width : source.Height,
            source.ColorType,
            source.AlphaType);

        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(turn);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);

        return result;
    }

    private static SKBitmap? Adjust(SKBitmap source, ImageEdit edit)
    {
        if (!edit.Grayscale && edit.Brightness == 0 && edit.Contrast == 0)
        {
            return null;
        }

        var result = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);

        using var canvas = new SKCanvas(result);
        using var filter = SKColorFilter.CreateColorMatrix(ColorMatrix(edit));
        using var paint = new SKPaint { ColorFilter = filter };

        canvas.DrawBitmap(source, 0, 0, paint);

        return result;
    }

    /// <summary>
    /// Grey first (Rec. 709 luminance), then contrast around the middle grey,
    /// then brightness. Translation is on Skia's 0-1 scale.
    /// </summary>
    internal static float[] ColorMatrix(ImageEdit edit)
    {
        var contrast = (100f + edit.Contrast) / 100f;
        var offset = ((1f - contrast) / 2f) + (edit.Brightness / 200f);

        float[] grey = edit.Grayscale
            ? [0.2126f, 0.7152f, 0.0722f]
            : [1f, 0f, 0f];

        float[] Row(int channel) => edit.Grayscale
            ? [grey[0] * contrast, grey[1] * contrast, grey[2] * contrast, 0, offset]
            : [channel == 0 ? contrast : 0, channel == 1 ? contrast : 0, channel == 2 ? contrast : 0, 0, offset];

        return [.. Row(0), .. Row(1), .. Row(2), 0, 0, 0, 1, 0];
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "An edit's working copy could not be deleted.");
        }
    }
}
