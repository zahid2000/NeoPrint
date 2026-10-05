using NeoPrint.Layout;

using SkiaSharp;

namespace NeoPrint.Kiosk.Printing;

/// <summary>A flash drive's picture, ready for the layout editor.</summary>
/// <param name="Aspect">Width over height, turned upright as its EXIF orientation says.</param>
/// <param name="Preview">A small upright copy for the screen (JPEG or PNG).</param>
public sealed record LayoutPicture(byte[] Bytes, double Aspect, byte[] Preview);

/// <summary>
/// "Səhifədə yerləşdir" at the kiosk (plan section 2.11): the picture is read
/// once and the layout drawn with the backend's own renderer, so a card
/// placed here prints the same size as one placed in the app.
/// </summary>
public static class KioskLayouts
{
    /// <summary>The one picture of a flash order, as the layout names it.</summary>
    public const string ImageKey = "photo";

    // The screen copy: 1200 px on its long side at the renderer's 300 dpi.
    private const double PreviewLongSideMm = 1200 / LayoutRenderer.MaxDpi * 25.4;

    /// <exception cref="PrintingException">The file is not a picture, or one too large (<c>layout.failed</c>).</exception>
    public static LayoutPicture Load(string imagePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(imagePath);
            var preview = LayoutRenderer.Prepare(bytes, PreviewLongSideMm, PreviewLongSideMm);

            using var codec = SKCodec.Create(new MemoryStream(preview)) ?? throw new PrintingException("layout.failed");

            return new LayoutPicture(bytes, (double)codec.Info.Width / codec.Info.Height, preview);
        }
        catch (LayoutException exception)
        {
            throw new PrintingException("layout.failed", exception);
        }
    }

    /// <summary>Draws the layout to a PDF of real millimetres.</summary>
    /// <exception cref="PrintingException">The layout is invalid or could not be drawn (<c>layout.failed</c>).</exception>
    public static void Render(LayoutProject project, byte[] picture, string pdfPath)
    {
        try
        {
            using var output = File.Create(pdfPath);
            LayoutRenderer.Render(project, key => key == ImageKey ? picture : null, output);
        }
        catch (LayoutException exception)
        {
            File.Delete(pdfPath);
            throw new PrintingException("layout.failed", exception);
        }
    }

    /// <summary>The paper a layout must print on at 100%: its own.</summary>
    public static string RealSizePaper(LayoutProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Paper == LayoutPaper.A3 ? "A3" : "A4";
    }
}
