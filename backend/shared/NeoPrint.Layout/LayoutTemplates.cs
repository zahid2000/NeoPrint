namespace NeoPrint.Layout;

/// <summary>A document type the scanner offers, with its real size (plan section 2.11).</summary>
/// <param name="Key">Stable name, shared with the mobile app.</param>
/// <param name="WidthMm">Width held upright (landscape cards are wider than tall).</param>
/// <param name="HeightMm">Height held upright.</param>
/// <param name="Sensitive">A personal document: encrypted, deleted after printing by default.</param>
public sealed record DocumentPreset(string Key, double WidthMm, double HeightMm, bool Sensitive);

/// <summary>The scanner's document types (plan section 2.11). "Custom" is not here: its size is the user's.</summary>
public static class DocumentPresets
{
    /// <summary>ISO/IEC 7810 ID-1: identity card, driving licence, bank card.</summary>
    public static readonly DocumentPreset IdCard = new("IdCard", 85.6, 53.98, Sensitive: true);

    public static readonly DocumentPreset Id2 = new("Id2", 105, 74, Sensitive: true);

    /// <summary>ISO/IEC 7810 ID-3: a passport page.</summary>
    public static readonly DocumentPreset Passport = new("Passport", 125, 88, Sensitive: true);

    public static readonly DocumentPreset Photo3x4 = new("Photo3x4", 30, 40, Sensitive: false);

    public static readonly DocumentPreset Photo10x15 = new("Photo10x15", 100, 150, Sensitive: false);

    public static readonly DocumentPreset A6 = new("A6", 105, 148, Sensitive: false);

    public static readonly DocumentPreset A5 = new("A5", 148, 210, Sensitive: false);

    public static readonly DocumentPreset A4 = new("A4", 210, 297, Sensitive: false);

    public static IReadOnlyList<DocumentPreset> All { get; } = [IdCard, Id2, Passport, Photo3x4, Photo10x15, A6, A5, A4];

    public static DocumentPreset? Find(string? key)
        => All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.Ordinal));
}

/// <summary>The ready-made arrangements (plan section 2.11).</summary>
public enum LayoutTemplate
{
    /// <summary>Front and back of a card on one page, one above the other, centred.</summary>
    IdFrontBack = 0,

    /// <summary>Four pictures in a 2 × 2 grid.</summary>
    Grid2x2 = 1,

    /// <summary>Eight cards, 2 across and 4 down.</summary>
    Cards2x4 = 2,

    /// <summary>Six 3 × 4 photos, 3 across and 2 down.</summary>
    Photo3x4x6 = 3,

    /// <summary>One passport page in the middle.</summary>
    PassportCentered = 4,
}

/// <summary>A picture for a template, at the size it should print.</summary>
public sealed record TemplatePicture(string Image, double WidthMm, double HeightMm);

/// <summary>
/// Builds the ready-made arrangements and the default one-per-page layout.
/// </summary>
/// <remarks>
/// Pictures go in at their real size and are only ever reduced - uniformly,
/// all together - when the arrangement would not fit inside the margins; an ID
/// card is never enlarged (plan section 2.11). The mobile app has the same
/// rules in Dart and checks itself against <c>backend/tests/fixtures/layout/templates.json</c>.
/// </remarks>
public static class LayoutTemplates
{
    private const double CardGapMm = 10;
    private const double GridGapMm = 5;

    /// <summary>Arranges the pictures by a template on A4, portrait.</summary>
    /// <param name="pictures">
    /// The pictures in order. Grids are filled by going round them again: one
    /// photo fills "Foto 3×4 × 6" with six copies.
    /// </param>
    public static LayoutProject Apply(LayoutTemplate template, IReadOnlyList<TemplatePicture> pictures, double marginMm)
    {
        ArgumentNullException.ThrowIfNull(pictures);

        if (pictures.Count == 0)
        {
            throw new LayoutException("layout.noPictures");
        }

        var page = template switch
        {
            LayoutTemplate.IdFrontBack => Grid(1, Math.Min(2, pictures.Count), pictures, CardGapMm, marginMm, repeat: false),
            LayoutTemplate.Grid2x2 => Grid(2, 2, pictures, GridGapMm, marginMm, repeat: true),
            LayoutTemplate.Cards2x4 => Grid(2, 4, pictures, GridGapMm, marginMm, repeat: true),
            LayoutTemplate.Photo3x4x6 => Grid(3, 2, pictures, GridGapMm, marginMm, repeat: true),
            LayoutTemplate.PassportCentered => Grid(1, 1, pictures, 0, marginMm, repeat: false),
            _ => throw new LayoutException("layout.template"),
        };

        return new LayoutProject(LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, marginMm, [page]);
    }

    /// <summary>
    /// The layout a scan gets before the user arranges anything: each picture
    /// on its own A4 page, centred, at real size or reduced to fit the margins.
    /// </summary>
    public static LayoutProject OnePerPage(IReadOnlyList<TemplatePicture> pictures, double marginMm)
    {
        ArgumentNullException.ThrowIfNull(pictures);

        if (pictures.Count == 0)
        {
            throw new LayoutException("layout.noPictures");
        }

        var pages = pictures.Select(picture => Grid(1, 1, [picture], 0, marginMm, repeat: false)).ToList();

        return new LayoutProject(LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, marginMm, pages);
    }

    /// <summary>
    /// A block of equal cells, each as large as the largest picture, centred
    /// on an A4 portrait page; each picture is centred in its cell.
    /// </summary>
    private static LayoutPage Grid(
        int columns, int rows, IReadOnlyList<TemplatePicture> pictures, double gapMm, double marginMm, bool repeat)
    {
        const double PageWidth = 210;
        const double PageHeight = 297;

        var count = repeat ? columns * rows : Math.Min(columns * rows, pictures.Count);
        var placed = Enumerable.Range(0, count).Select(index => pictures[index % pictures.Count]).ToList();

        var cellWidth = placed.Max(picture => picture.WidthMm);
        var cellHeight = placed.Max(picture => picture.HeightMm);
        var blockWidth = (columns * cellWidth) + ((columns - 1) * gapMm);
        var blockHeight = (rows * cellHeight) + ((rows - 1) * gapMm);

        var usableWidth = PageWidth - (2 * marginMm);
        var usableHeight = PageHeight - (2 * marginMm);
        var scale = Math.Min(1.0, Math.Min(usableWidth / blockWidth, usableHeight / blockHeight));

        var left = (PageWidth - (blockWidth * scale)) / 2;
        var top = (PageHeight - (blockHeight * scale)) / 2;
        var elements = new List<LayoutElement>(count);

        for (var index = 0; index < count; index++)
        {
            var picture = placed[index];
            var column = index % columns;
            var row = index / columns;
            var width = picture.WidthMm * scale;
            var height = picture.HeightMm * scale;
            var cellLeft = left + (column * (cellWidth + gapMm) * scale);
            var cellTop = top + (row * (cellHeight + gapMm) * scale);

            elements.Add(new LayoutElement(
                picture.Image,
                LayoutGeometry.Round(cellLeft + (((cellWidth * scale) - width) / 2)),
                LayoutGeometry.Round(cellTop + (((cellHeight * scale) - height) / 2)),
                LayoutGeometry.Round(width),
                LayoutGeometry.Round(height)));
        }

        return new LayoutPage(elements);
    }
}
