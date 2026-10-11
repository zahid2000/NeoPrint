
using PdfSharp.Drawing;
using PdfSharp.Pdf;

using SkiaSharp;

namespace NeoPrint.Layout;

/// <summary>
/// Turns a <see cref="LayoutProject"/> into a PDF at true physical size
/// (plan section 2.11).
/// </summary>
/// <remarks>
/// <para>
/// One renderer for the backend (mobile layouts and scans) and the kiosk (its
/// own editor), so the same JSON prints the same on both: the pages are drawn
/// identically and every picture is re-encoded the same way. Only the file's
/// own metadata - its dates and ids, which PDFsharp sets - differs between runs.
/// </para>
/// <para>
/// A picture is placed in its box in millimetres; nothing is scaled to fit.
/// Pictures are reduced to at most <see cref="MaxDpi"/> at the largest size
/// they are placed at - more is invisible on a kiosk printer and only makes
/// the file slow to send - and turned upright first if the camera recorded
/// an orientation instead of turning the pixels.
/// </para>
/// </remarks>
public static class LayoutRenderer
{
    /// <summary>The highest resolution a picture is kept at (plan section 2.11).</summary>
    public const double MaxDpi = 300;

    /// <summary>
    /// The largest picture accepted, in pixels - the same limit as the upload
    /// pipeline, checked from the header before anything is decoded.
    /// </summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>The most different pictures in one layout - one per page of the longest scan.</summary>
    public const int MaxImages = LayoutJson.MaxPages;

    private const int JpegQuality = 90;


    /// <summary>Draws the layout and writes the PDF to <paramref name="output"/>.</summary>
    /// <param name="images">The encoded bytes (JPEG, PNG, WEBP...) for an element's <see cref="LayoutElement.Image"/>; null when there is none.</param>
    /// <exception cref="LayoutException">The layout is invalid, or a picture is missing, unreadable or too large.</exception>
    public static void Render(LayoutProject project, Func<string, byte[]?> images, Stream output)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(output);

        LayoutJson.Validate(project);

        var keys = project.Images();

        if (keys.Count > MaxImages)
        {
            throw new LayoutException("layout.imageCount");
        }

        var (pageWidth, pageHeight) = project.PageSizeMm();
        var longest = Math.Max(pageWidth, pageHeight);
        var streams = new List<MemoryStream>();
        var prepared = new Dictionary<string, XImage>(StringComparer.Ordinal);

        try
        {
            foreach (var key in keys)
            {
                var placed = project.Pages.SelectMany(page => page.Elements).Where(element => element.Image == key).ToList();

                // A picture bigger than the paper is cut off anyway; resolution
                // beyond twice the paper's long side is never seen.
                var widthMm = Math.Min(placed.Max(element => element.WidthMm), 2 * longest);
                var heightMm = Math.Min(placed.Max(element => element.HeightMm), 2 * longest);
                var encoded = images(key) ?? throw new LayoutException("layout.imageMissing");
                var bytes = Prepare(encoded, widthMm, heightMm);
                var stream = new MemoryStream(bytes, writable: false);
                streams.Add(stream);
                prepared[key] = XImage.FromStream(stream);
            }

            using var document = new PdfDocument();

            // PDFsharp 6.2 ends the streams it compresses without the final
            // block and checksum (T-198). A page here is a few drawing
            // operators; the pictures are JPEG and are not touched by this.
            document.Options.CompressContentStreams = false;


            foreach (var page in project.Pages)
            {
                var pdfPage = document.AddPage();
                pdfPage.Width = XUnit.FromMillimeter(pageWidth);
                pdfPage.Height = XUnit.FromMillimeter(pageHeight);

                using var graphics = XGraphics.FromPdfPage(pdfPage);

                foreach (var element in page.Elements)
                {
                    Draw(graphics, prepared[element.Image], element);
                }
            }

            document.Save(output, closeStream: false);
        }
        finally
        {
            foreach (var image in prepared.Values)
            {
                image.Dispose();
            }

            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private static void Draw(XGraphics graphics, XImage image, LayoutElement element)
    {
        var width = element.WidthMm * LayoutGeometry.PointsPerMm;
        var height = element.HeightMm * LayoutGeometry.PointsPerMm;
        var centreX = (element.XMm + (element.WidthMm / 2)) * LayoutGeometry.PointsPerMm;
        var centreY = (element.YMm + (element.HeightMm / 2)) * LayoutGeometry.PointsPerMm;

        var state = graphics.Save();
        graphics.TranslateTransform(centreX, centreY);
        graphics.RotateTransform(element.RotationDeg % 360);
        graphics.DrawImage(image, -width / 2, -height / 2, width, height);
        graphics.Restore(state);
    }

    /// <summary>
    /// Decodes a picture, turns it upright, reduces it to <see cref="MaxDpi"/>
    /// for a box of the given size and re-encodes it: JPEG, or PNG when it has
    /// transparency.
    /// </summary>
    /// <exception cref="LayoutException">The bytes are not a picture, or one too large to decode.</exception>
    public static byte[] Prepare(byte[] encoded, double widthMm, double heightMm)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data) ?? throw new LayoutException("layout.imageUnreadable");

        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            throw new LayoutException("layout.imageTooLarge");
        }

        using var decoded = SKBitmap.Decode(codec) ?? throw new LayoutException("layout.imageUnreadable");
        using var upright = Orient(decoded, codec.EncodedOrigin);
        var source = upright ?? decoded;

        var maxWidth = Math.Max(1, (int)Math.Ceiling(widthMm / 25.4 * MaxDpi));
        var maxHeight = Math.Max(1, (int)Math.Ceiling(heightMm / 25.4 * MaxDpi));
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));

        using var resized = scale < 1.0
            ? source.Resize(
                new SKImageInfo(
                    Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale)),
                    source.ColorType,
                    source.AlphaType),
                SKFilterQuality.High)
            : null;

        var final = resized ?? source;
        var opaque = final.AlphaType == SKAlphaType.Opaque;

        using var image = SKImage.FromBitmap(final);
        using var output = image.Encode(opaque ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, opaque ? JpegQuality : 100)
            ?? throw new LayoutException("layout.imageUnreadable");

        return output.ToArray();
    }

    /// <summary>
    /// The picture turned as its EXIF orientation says, or null when it is
    /// already upright. Phones often store the pixels sideways and record the
    /// turn instead; printed as stored, the card would lie on its side.
    /// </summary>
    private static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        var width = source.Width;
        var height = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? height : width, swap ? width : height, source.ColorType, source.AlphaType);

        using var canvas = new SKCanvas(result);

        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);

        return result;
    }
}
