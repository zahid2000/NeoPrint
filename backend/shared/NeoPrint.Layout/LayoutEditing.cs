namespace NeoPrint.Layout;

/// <summary>A line a moved picture snapped to, for the editor to show.</summary>
/// <param name="Vertical">A vertical line at <paramref name="Mm"/> across; otherwise horizontal, down.</param>
public readonly record struct SnapLine(bool Vertical, double Mm);

/// <summary>
/// The layout editor's operations (plan section 2.11), each returning a new
/// layout - the kiosk's editor uses them; the mobile app has the same rules
/// in Dart (<c>mobile/lib/features/layout/layout_editing.dart</c>).
/// </summary>
public static class LayoutEditing
{
    /// <summary>How close an edge or centre must come to snap.</summary>
    public const double SnapMm = 2;

    /// <summary>Where a copy lands relative to the original.</summary>
    public const double DuplicateOffsetMm = 5;

    public static LayoutProject Replace(LayoutProject project, int page, int index, LayoutElement element)
    {
        ArgumentNullException.ThrowIfNull(project);

        return WithPage(project, page, [.. project.Pages[page].Elements.Select((current, i) => i == index ? element : current)]);
    }

    /// <summary>
    /// Moves a picture and snaps it to the paper's edges, margin and centre and
    /// to the other pictures, within <see cref="SnapMm"/>.
    /// </summary>
    public static (LayoutElement Element, IReadOnlyList<SnapLine> Lines) Move(
        LayoutProject project, int page, int index, LayoutElement start, double dxMm, double dyMm, bool snap = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(start);

        var moved = start with { XMm = start.XMm + dxMm, YMm = start.YMm + dyMm };

        if (!snap)
        {
            return (moved, []);
        }

        var (width, height) = project.PageSizeMm();
        var margin = project.MarginMm;
        var others = project.Pages[page].Elements.Where((_, i) => i != index).Select(LayoutGeometry.Bounds).ToList();

        double[] xTargets =
        [
            0, margin, width / 2, width - margin, width,
            .. others.SelectMany(box => new[] { box.Left, box.Right, (box.Left + box.Right) / 2 }),
        ];
        double[] yTargets =
        [
            0, margin, height / 2, height - margin, height,
            .. others.SelectMany(box => new[] { box.Top, box.Bottom, (box.Top + box.Bottom) / 2 }),
        ];

        var bounds = LayoutGeometry.Bounds(moved);
        var lines = new List<SnapLine>();

        if (Nearest([bounds.Left, bounds.Right, (bounds.Left + bounds.Right) / 2], xTargets) is var (dx, x))
        {
            moved = moved with { XMm = moved.XMm + dx };
            lines.Add(new SnapLine(true, x));
        }

        if (Nearest([bounds.Top, bounds.Bottom, (bounds.Top + bounds.Bottom) / 2], yTargets) is var (dy, y))
        {
            moved = moved with { YMm = moved.YMm + dy };
            lines.Add(new SnapLine(false, y));
        }

        return (Rounded(moved), lines);
    }

    private static (double Distance, double Target)? Nearest(double[] edges, double[] targets)
    {
        (double Distance, double Target)? best = null;

        foreach (var edge in edges)
        {
            foreach (var target in targets)
            {
                var distance = target - edge;

                if (Math.Abs(distance) <= SnapMm && (best is null || Math.Abs(distance) < Math.Abs(best.Value.Distance)))
                {
                    best = (distance, target);
                }
            }
        }

        return best;
    }

    /// <summary>Scales a picture about its centre by one factor, keeping its shape and the backend's limits.</summary>
    public static LayoutElement Scale(LayoutElement start, double factor)
    {
        ArgumentNullException.ThrowIfNull(start);

        var width = start.WidthMm * factor;
        var height = start.HeightMm * factor;
        var shrink = Math.Max(Math.Max(LayoutJson.MinSideMm / width, LayoutJson.MinSideMm / height), 1);
        var grow = Math.Min(Math.Min(LayoutJson.MaxSideMm / width, LayoutJson.MaxSideMm / height), 1);

        return Rounded(Centred(start, width * shrink * grow, height * shrink * grow));
    }

    /// <summary>
    /// Gives a picture a size, held the way it is now: a card sized 85.6 × 53.98
    /// stays upright if it was upright (plan section 2.11, real size).
    /// </summary>
    public static LayoutElement Resize(LayoutElement element, double widthMm, double heightMm)
    {
        ArgumentNullException.ThrowIfNull(element);

        var longSide = Math.Max(widthMm, heightMm);
        var shortSide = Math.Min(widthMm, heightMm);
        var landscape = element.WidthMm >= element.HeightMm;

        return Rounded(Centred(element, landscape ? longSide : shortSide, landscape ? shortSide : longSide));
    }

    /// <summary>A quarter turn clockwise, to the next right angle.</summary>
    public static LayoutElement Rotate90(LayoutElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return element with { RotationDeg = Normalise((Math.Floor(element.RotationDeg / 90) + 1) * 90) };
    }

    /// <summary>In the middle of the paper, both ways.</summary>
    public static LayoutElement Centre(LayoutProject project, LayoutElement element)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(element);

        var (width, height) = project.PageSizeMm();

        return Rounded(element with { XMm = (width - element.WidthMm) / 2, YMm = (height - element.HeightMm) / 2 });
    }

    /// <summary>A copy just below and right of the picture, on top of the others.</summary>
    public static LayoutProject Duplicate(LayoutProject project, int page, int index)
    {
        ArgumentNullException.ThrowIfNull(project);

        var element = project.Pages[page].Elements[index];

        return WithPage(project, page, [
            .. project.Pages[page].Elements,
            element with { XMm = element.XMm + DuplicateOffsetMm, YMm = element.YMm + DuplicateOffsetMm },
        ]);
    }

    public static LayoutProject Delete(LayoutProject project, int page, int index)
    {
        ArgumentNullException.ThrowIfNull(project);

        return WithPage(project, page, [.. project.Pages[page].Elements.Where((_, i) => i != index)]);
    }

    /// <summary>
    /// A picture of the given shape in the middle of the page, as large as fits
    /// inside the margins: a flash drive's photo has no known real size.
    /// </summary>
    public static LayoutProject Add(LayoutProject project, int page, string image, double aspect)
    {
        ArgumentNullException.ThrowIfNull(project);

        var (width, height) = project.PageSizeMm();
        var usableWidth = width - (2 * project.MarginMm);
        var usableHeight = height - (2 * project.MarginMm);
        var w = usableWidth;
        var h = usableWidth / aspect;
        var fit = Math.Min(1, Math.Min(usableWidth / w, usableHeight / h));
        var element = Centre(project, new LayoutElement(image, 0, 0, w * fit, h * fit));

        return WithPage(project, page, [.. project.Pages[page].Elements, element]);
    }

    /// <summary>Pictures whose centre is off the paper: the renderer refuses them.</summary>
    public static bool HasOffPage(LayoutProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var (width, height) = project.PageSizeMm();

        return project.Pages.Any(page => page.Elements.Any(element =>
        {
            var x = element.XMm + (element.WidthMm / 2);
            var y = element.YMm + (element.HeightMm / 2);
            return x < 0 || y < 0 || x > width || y > height;
        }));
    }

    /// <summary>Whether any picture reaches where a printer cannot print (a warning).</summary>
    public static bool LeavesPrintableArea(LayoutProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Pages.Any(page => page.Elements.Any(element => LayoutGeometry.LeavesPrintableArea(project, element)));
    }

    private static LayoutElement Centred(LayoutElement element, double width, double height)
    {
        var cx = element.XMm + (element.WidthMm / 2);
        var cy = element.YMm + (element.HeightMm / 2);

        return element with { XMm = cx - (width / 2), YMm = cy - (height / 2), WidthMm = width, HeightMm = height };
    }

    private static LayoutElement Rounded(LayoutElement element) => element with
    {
        XMm = LayoutGeometry.Round(element.XMm),
        YMm = LayoutGeometry.Round(element.YMm),
        WidthMm = LayoutGeometry.Round(element.WidthMm),
        HeightMm = LayoutGeometry.Round(element.HeightMm),
    };

    private static double Normalise(double degrees)
    {
        var value = degrees % 360;
        return value < 0 ? value + 360 : value;
    }

    private static LayoutProject WithPage(LayoutProject project, int page, IReadOnlyList<LayoutElement> elements)
        => project with { Pages = [.. project.Pages.Select((current, i) => i == page ? new LayoutPage(elements) : current)] };
}
