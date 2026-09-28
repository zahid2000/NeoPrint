using System.Text;

using NeoPrint.Kiosk.Core.Printing;

using SkiaSharp;

namespace NeoPrint.Kiosk.Printing;

/// <summary>An image edit from the file editor (plan section 2.2): turn, cut, lighten.</summary>
/// <param name="Crop">Fractions of the image, 0–1; null keeps it whole.</param>
public sealed record ImageChange(int Rotate, SKRect? Crop, int Brightness, int Contrast);

/// <summary>
/// Images, plain text and the test page, drawn with SkiaSharp.
/// </summary>
/// <remarks>
/// Pixel counts are checked from the header before anything is decoded: a
/// small file can claim a huge image (a decompression bomb), and the kiosk
/// has no memory to spare (the backend's limit, plan section 2.2).
/// </remarks>
public static class SkiaDocuments
{
    /// <summary>The largest image accepted, in pixels (the backend's limit).</summary>
    public const long MaxPixels = 100_000_000;

    private static readonly SKSize A4 = new(595.28f, 841.89f);

    /// <summary>An image on a page of its own size, as the backend makes it (96 dpi).</summary>
    public static void ImageToPdf(string imagePath, string pdfPath)
    {
        using var bitmap = DecodeChecked(imagePath);

        var width = bitmap.Width * 72f / 96f;
        var height = bitmap.Height * 72f / 96f;

        using var stream = new SKFileWStream(pdfPath);
        using var document = SKDocument.CreatePdf(stream);

        var canvas = document.BeginPage(width, height);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height));
        document.EndPage();
        document.Close();
    }

    /// <summary>
    /// A monospaced face that has every letter of the text (ə, ğ, kiril), so no
    /// letter prints as an empty box; the first installed one when none has all.
    /// </summary>
    public static SKTypeface TypefaceFor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var letters = new string([.. text.Where(character => !char.IsControl(character) && !char.IsSurrogate(character)).Distinct()]);
        SKTypeface? first = null;

        foreach (var family in MonospacedFamilies)
        {
            var typeface = SKTypeface.FromFamilyName(family);

            if (typeface is null || !string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                // Skia falls back to some other face for a family it lacks.
                typeface?.Dispose();
                continue;
            }

            if (typeface.ContainsGlyphs(letters))
            {
                first?.Dispose();
                return typeface;
            }

            if (first is null)
            {
                first = typeface;
            }
            else
            {
                typeface.Dispose();
            }
        }

        return first ?? SKTypeface.Default;
    }

    private static readonly string[] MonospacedFamilies =
        ["Consolas", "DejaVu Sans Mono", "Cascadia Mono", "Liberation Mono", "Noto Sans Mono", "Courier New"];

    /// <summary>
    /// A text file on A4 pages in a monospaced face, wrapped at the margin.
    /// </summary>
    /// <remarks>
    /// The kiosk does this itself rather than through LibreOffice: it is the
    /// format people edit at the kiosk (plan section 2.2), and it keeps
    /// working on a kiosk whose LibreOffice is missing or broken.
    /// </remarks>
    public static void TextToPdf(string text, string pdfPath)
    {
        ArgumentNullException.ThrowIfNull(text);

        const float Margin = 56;
        const float Size = 10.5f;

        using var typeface = TypefaceFor(text);
        using var font = new SKFont(typeface, Size);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var lineHeight = Size * 1.35f;
        var usable = A4.Width - (2 * Margin);
        using var measure = new SKPaint { Typeface = typeface, TextSize = Size };
        var lines = Wrap(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\t', ' '), measure, usable);
        var perPage = (int)((A4.Height - (2 * Margin)) / lineHeight);

        using var stream = new SKFileWStream(pdfPath);
        using var document = SKDocument.CreatePdf(stream);

        for (var start = 0; start < Math.Max(1, lines.Count); start += perPage)
        {
            var canvas = document.BeginPage(A4.Width, A4.Height);

            for (var index = 0; index < perPage && start + index < lines.Count; index++)
            {
                canvas.DrawText(lines[start + index], Margin, Margin + ((index + 1) * lineHeight), font, paint);
            }

            document.EndPage();
        }

        document.Close();
    }

    /// <summary>An edited copy of an image, in its own format where it can be written.</summary>
    public static void EditImage(string sourcePath, string targetPath, ImageChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        using var bitmap = DecodeChecked(sourcePath);
        using var cropped = Crop(bitmap, change.Crop);
        using var rotated = Rotate(cropped ?? bitmap, change.Rotate);
        var source = rotated ?? cropped ?? bitmap;

        using var surface = SKSurface.Create(new SKImageInfo(source.Width, source.Height));
        using var paint = new SKPaint();

        if (change.Brightness != 0 || change.Contrast != 0)
        {
            paint.ColorFilter = Adjustment(change.Brightness, change.Contrast);
        }

        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawBitmap(source, 0, 0, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new PrintingException("edit.imageUnwritable");
        using var output = File.Create(targetPath);
        data.SaveTo(output);
    }

    /// <summary>The first page of an image, for its preview.</summary>
    public static (int Width, int Height) ImageSize(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new PrintingException("conversion.imageUnreadable");
        return (codec.Info.Width, codec.Info.Height);
    }

    private static SKBitmap DecodeChecked(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new PrintingException("conversion.imageUnreadable");

        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            throw new PrintingException("conversion.imageTooLarge");
        }

        return SKBitmap.Decode(codec) ?? throw new PrintingException("conversion.imageUnsupported");
    }

    private static SKBitmap? Crop(SKBitmap source, SKRect? crop)
    {
        if (crop is not { } area)
        {
            return null;
        }

        var rect = SKRectI.Create(
            (int)Math.Round(area.Left * source.Width),
            (int)Math.Round(area.Top * source.Height),
            Math.Max(1, (int)Math.Round(area.Width * source.Width)),
            Math.Max(1, (int)Math.Round(area.Height * source.Height)));

        rect.Intersect(SKRectI.Create(0, 0, source.Width, source.Height));

        using var subset = new SKBitmap();

        return source.ExtractSubset(subset, rect)
            ? subset.Copy()
            : throw new PrintingException("edit.cropFailed");
    }

    private static SKBitmap? Rotate(SKBitmap source, int degrees)
    {
        var turn = ((degrees % 360) + 360) % 360;

        if (turn == 0)
        {
            return null;
        }

        var sideways = turn is 90 or 270;
        var result = new SKBitmap(sideways ? source.Height : source.Width, sideways ? source.Width : source.Height);

        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(turn);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);

        return result;
    }

    /// <summary>Brightness and contrast, -100…100 each, as one colour matrix.</summary>
    private static SKColorFilter Adjustment(int brightness, int contrast)
    {
        var c = 1f + (Math.Clamp(contrast, -100, 100) / 100f);
        var b = Math.Clamp(brightness, -100, 100) * 2.55f / 255f;
        var t = ((1f - c) / 2f) + b;

        return SKColorFilter.CreateColorMatrix(
        [
            c, 0, 0, 0, t,
            0, c, 0, 0, t,
            0, 0, c, 0, t,
            0, 0, 0, 1, 0,
        ]);
    }

    private static List<string> Wrap(string text, SKPaint font, float width)
    {
        var lines = new List<string>();

        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();

            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;

                if (font.MeasureText(candidate) <= width || line.Length == 0)
                {
                    line.Clear().Append(candidate);

                    // A word longer than the line is cut where it must be.
                    while (font.MeasureText(line.ToString()) > width && line.Length > 1)
                    {
                        var fit = line.Length - 1;
                        while (fit > 1 && font.MeasureText(line.ToString(0, fit)) > width)
                        {
                            fit--;
                        }

                        lines.Add(line.ToString(0, fit));
                        line.Remove(0, fit);
                    }

                    continue;
                }

                lines.Add(line.ToString());
                line.Clear().Append(word);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }
}

/// <summary>
/// The test page (plan section 7.7): the lines the kiosk gives it (name, code,
/// printer, time, capabilities), a grey ramp and colour patches.
/// </summary>
public sealed class SkiaTestPageSource : ITestPageSource
{
    public Task<string> CreateAsync(string folder, TestPageInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        var path = Path.Combine(folder, "test-page.pdf");
        var size = new SKSize(595.28f, 841.89f);

        using var typeface = SKTypeface.Default;
        using var title = new SKFont(typeface, 26) { Embolden = true };
        using var body = new SKFont(typeface, 14);
        using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        using var stream = new SKFileWStream(path);
        using var document = SKDocument.CreatePdf(stream);

        var canvas = document.BeginPage(size.Width, size.Height);

        for (var index = 0; index < info.Lines.Count; index++)
        {
            canvas.DrawText(info.Lines[index], 56, index == 0 ? 90 : 105 + (index * 25), index == 0 ? title : body, ink);
        }

        // A grey ramp: every printer should show ten distinct steps.
        for (var step = 0; step < 10; step++)
        {
            using var grey = new SKPaint { Color = new SKColor((byte)(step * 25), (byte)(step * 25), (byte)(step * 25)) };
            canvas.DrawRect(56 + (step * 48), 220, 44, 44, grey);
        }

        // Colour patches; on a mono printer they come out as greys, which is right.
        SKColor[] colours = [SKColors.Cyan, SKColors.Magenta, SKColors.Yellow, SKColors.Red, SKColors.Green, SKColors.Blue];

        for (var index = 0; index < colours.Length; index++)
        {
            using var patch = new SKPaint { Color = colours[index] };
            canvas.DrawRect(56 + (index * 80), 290, 72, 72, patch);
        }

        // A frame at 10 mm from the edge: where it is cut off shows the
        // printer's margins.
        using var frame = new SKPaint { Color = SKColors.Black, IsStroke = true, StrokeWidth = 1 };
        canvas.DrawRect(28.35f, 28.35f, size.Width - 56.7f, size.Height - 56.7f, frame);

        document.EndPage();
        document.Close();

        return Task.FromResult(path);
    }
}
