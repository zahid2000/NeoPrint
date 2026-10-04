namespace NeoPrint.Layout;

/// <summary>The paper a layout is drawn for (plan section 2.11: A4 / A3, plus A5).</summary>
public enum LayoutPaper
{
    A4 = 0,
    A3 = 1,
    A5 = 2,
}

public enum LayoutOrientation
{
    Portrait = 0,
    Landscape = 1,
}

/// <summary>
/// One picture placed on a page, in millimetres from the paper's top-left corner.
/// </summary>
/// <param name="Image">
/// Which picture: a key the caller resolves to the image's bytes. The renderer
/// never interprets it - the backend uses its own asset ids, the kiosk a local
/// file name.
/// </param>
/// <param name="XMm">Left edge of the unrotated box.</param>
/// <param name="YMm">Top edge of the unrotated box.</param>
/// <param name="WidthMm">Width of the unrotated box.</param>
/// <param name="HeightMm">Height of the unrotated box.</param>
/// <param name="RotationDeg">
/// Clockwise turn about the box's centre, in degrees. The box's size is the
/// printed size of the picture whatever the angle: an ID card stays
/// 85.6 × 53.98 mm when turned.
/// </param>
public sealed record LayoutElement(
    string Image,
    double XMm,
    double YMm,
    double WidthMm,
    double HeightMm,
    double RotationDeg = 0);

/// <summary>One sheet side. Later elements are drawn over earlier ones.</summary>
public sealed record LayoutPage(IReadOnlyList<LayoutElement> Elements);

/// <summary>
/// A page layout (plan section 2.11, <c>LayoutProjectJson</c>): pictures at
/// physical size on paper.
/// </summary>
/// <remarks>
/// The same JSON is written by the mobile editor, the kiosk editor and the
/// templates, and turned into a PDF by <see cref="LayoutRenderer"/> on the
/// backend and on the kiosk, so the two print the same thing.
/// </remarks>
/// <param name="Version">Format version; <see cref="CurrentVersion"/>.</param>
/// <param name="Paper">Paper size of every page.</param>
/// <param name="Orientation">Orientation of every page.</param>
/// <param name="MarginMm">The margin the editor keeps to; the renderer does not clip to it.</param>
/// <param name="Pages">The pages, in print order.</param>
public sealed record LayoutProject(
    int Version,
    LayoutPaper Paper,
    LayoutOrientation Orientation,
    double MarginMm,
    IReadOnlyList<LayoutPage> Pages)
{
    public const int CurrentVersion = 1;

    /// <summary>Width and height of the paper as it lies, in millimetres.</summary>
    public (double Width, double Height) PageSizeMm()
    {
        var (shortSide, longSide) = Paper switch
        {
            LayoutPaper.A3 => (297.0, 420.0),
            LayoutPaper.A5 => (148.0, 210.0),
            _ => (210.0, 297.0),
        };

        return Orientation == LayoutOrientation.Landscape ? (longSide, shortSide) : (shortSide, longSide);
    }

    /// <summary>Every picture the layout uses, once each.</summary>
    public IReadOnlyList<string> Images()
        => [.. Pages.SelectMany(page => page.Elements).Select(element => element.Image).Distinct(StringComparer.Ordinal)];
}

/// <summary>A layout that cannot be drawn. <see cref="Exception.Message"/> is a localisation key.</summary>
public sealed class LayoutException : Exception
{
    public LayoutException()
    {
    }

    public LayoutException(string key)
        : base(key)
    {
    }

    public LayoutException(string key, Exception innerException)
        : base(key, innerException)
    {
    }
}
