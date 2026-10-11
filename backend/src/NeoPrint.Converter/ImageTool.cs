using SkiaSharp;

namespace NeoPrint.Converter;

/// <summary>
/// Raster images with SkiaSharp: wrapped into a one-page PDF, and edited
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// Every image is held to <see cref="ToolContract.MaxImagePixels"/>, read from
/// the header before anything is decoded - that is what keeps a decompression
/// bomb from mattering, with the process's memory limit behind it.
/// </remarks>
internal static class ImageTool
{
    /// <summary>
    /// The image on a page of its own size at 96 dpi rather than stretched
    /// onto A4: the print options decide the paper, and scaling twice would
    /// soften the result.
    /// </summary>
    public static ToolResponse ToPdf(string directory, ToolRequest request)
    {
        var input = ToolFiles.Existing(directory, request.Input);
        var output = ToolFiles.Local(directory, request.Output);

        using var codec = SKCodec.Create(input) ?? throw new ToolException("conversion.imageUnreadable");

        if ((long)codec.Info.Width * codec.Info.Height > ToolContract.MaxImagePixels)
        {
            throw new ToolException("conversion.imageTooLarge");
        }

        using var bitmap = SKBitmap.Decode(codec) ?? throw new ToolException("conversion.imageUnsupported");

        // 96 dpi is what a screen pixel means on paper; the print options
        // decide the actual paper size later.
        var widthPoints = bitmap.Width * 72f / 96f;
        var heightPoints = bitmap.Height * 72f / 96f;

        try
        {
            using var stream = new SKFileWStream(output);
            using var document = SKDocument.CreatePdf(stream) ?? throw new ToolException("conversion.imageToPdfFailed");

            var canvas = document.BeginPage(widthPoints, heightPoints);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, widthPoints, heightPoints));
            document.EndPage();
            document.Close();
        }
        catch (Exception exception) when (exception is not ToolException)
        {
            throw new ToolException("conversion.imageToPdfFailed");
        }

        return new ToolResponse(true);
    }

    /// <summary>Crop, rotate, grey, light - in that order - written in the input's own format where Skia can.</summary>
    public static ToolResponse Edit(string directory, ToolRequest request)
    {
        var input = ToolFiles.Existing(directory, request.Input);
        var output = ToolFiles.Local(directory, request.Output);
        var edit = request.Image ?? throw new ToolUsageException("An image edit needs its settings.");

        using var codec = SKCodec.Create(input) ?? throw new ToolException("conversion.imageUnreadable");

        if ((long)codec.Info.Width * codec.Info.Height > ToolContract.MaxImagePixels)
        {
            throw new ToolException("edit.imageTooLarge");
        }

        var (format, extension) = OutputFormat(codec.EncodedFormat);

        // One bitmap at a time: each step's input is released as soon as its
        // result exists, so a large photo is held twice at most.
        var current = SKBitmap.Decode(codec) ?? throw new ToolException("edit.imageUnopenable");

        try
        {
            current = Replace(current, Crop(current, edit.Crop));
            current = Replace(current, Rotate(current, edit.Rotate));
            current = Replace(current, Adjust(current, edit));

            using var image = SKImage.FromBitmap(current);
            using var data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 92 : 100)
                             ?? throw new ToolException("edit.imageUnwritable");
            using var file = File.Create(output);

            data.SaveTo(file);
        }
        finally
        {
            current.Dispose();
        }

        return new ToolResponse(true, Extension: extension);
    }

    private static SKBitmap Replace(SKBitmap current, SKBitmap? next)
    {
        if (next is null)
        {
            return current;
        }

        current.Dispose();
        return next;
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

    private static SKBitmap? Crop(SKBitmap source, ToolImageCrop? crop)
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

        using var subset = new SKBitmap(rect.Width, rect.Height, source.ColorType, source.AlphaType);

        if (!source.ExtractSubset(subset, rect))
        {
            throw new ToolException("edit.cropFailed");
        }

        // ExtractSubset shares pixels with the source; a copy stands alone.
        return subset.Copy() ?? throw new ToolException("edit.cropFailed");
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

    private static SKBitmap? Adjust(SKBitmap source, ToolImageEdit edit)
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
    internal static float[] ColorMatrix(ToolImageEdit edit)
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
}
