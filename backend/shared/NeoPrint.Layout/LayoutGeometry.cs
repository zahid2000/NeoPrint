namespace NeoPrint.Layout;

/// <summary>An axis-aligned rectangle in millimetres.</summary>
public readonly record struct RectMm(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

/// <summary>
/// Measurements the editors share: where a turned picture reaches, and whether
/// it leaves the area a printer can reach (plan section 2.11).
/// </summary>
public static class LayoutGeometry
{
    /// <summary>
    /// The edge a printer cannot print into when it does not say: 4 mm (plan section 2.11).
    /// </summary>
    public const double DefaultPrinterMarginMm = 4;

    /// <summary>1 mm in PDF points (1/72 inch).</summary>
    public const double PointsPerMm = 72 / 25.4;

    /// <summary>The smallest upright rectangle holding the picture as turned.</summary>
    public static RectMm Bounds(LayoutElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var radians = element.RotationDeg * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var width = (element.WidthMm * cos) + (element.HeightMm * sin);
        var height = (element.WidthMm * sin) + (element.HeightMm * cos);
        var centreX = element.XMm + (element.WidthMm / 2);
        var centreY = element.YMm + (element.HeightMm / 2);

        return new RectMm(centreX - (width / 2), centreY - (height / 2), centreX + (width / 2), centreY + (height / 2));
    }

    /// <summary>
    /// Whether part of the picture falls where the printer cannot print, so
    /// the editor can warn before it is cut off.
    /// </summary>
    /// <param name="printerMarginMm">The printer's own unprintable edge; <see cref="DefaultPrinterMarginMm"/> when unknown.</param>
    public static bool LeavesPrintableArea(LayoutProject project, LayoutElement element, double printerMarginMm = DefaultPrinterMarginMm)
    {
        ArgumentNullException.ThrowIfNull(project);

        // A hundredth of a millimetre either way is rounding, not a picture
        // over the edge.
        const double Tolerance = 0.01;

        var (width, height) = project.PageSizeMm();
        var bounds = Bounds(element);

        return bounds.Left < printerMarginMm - Tolerance
            || bounds.Top < printerMarginMm - Tolerance
            || bounds.Right > width - printerMarginMm + Tolerance
            || bounds.Bottom > height - printerMarginMm + Tolerance;
    }

    /// <summary>Rounds to a hundredth of a millimetre, the precision layouts are stored at.</summary>
    public static double Round(double millimetres) => Math.Round(millimetres, 2, MidpointRounding.AwayFromZero);
}
