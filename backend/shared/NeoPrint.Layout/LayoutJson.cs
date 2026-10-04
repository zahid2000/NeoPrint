using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoPrint.Layout;

/// <summary>
/// Reads, checks and writes <see cref="LayoutProject"/> JSON.
/// </summary>
/// <remarks>
/// The JSON comes from phones and kiosks, so it is untrusted: every number is
/// checked before anything is drawn, and the counts are capped so a request
/// cannot ask for a thousand pages of a thousand pictures.
/// </remarks>
public static class LayoutJson
{
    /// <summary>Most pages one layout may have.</summary>
    public const int MaxPages = 50;

    /// <summary>Most pictures on one page ("8 kart" needs 8).</summary>
    public const int MaxElementsPerPage = 40;

    /// <summary>Largest margin the editor offers.</summary>
    public const double MaxMarginMm = 50;

    /// <summary>Smallest picture side: anything less cannot be seen on paper.</summary>
    public const double MinSideMm = 1;

    /// <summary>Longest picture side: well past A3, so a picture can be enlarged to bleed off the page.</summary>
    public const double MaxSideMm = 1000;

    public const int MaxImageKeyLength = 100;

    /// <summary>A layout's JSON, at most. 50 pages × 40 pictures fit easily.</summary>
    public const int MaxJsonLength = 512 * 1024;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 8,
    };

    /// <summary>Parses and checks a layout.</summary>
    /// <exception cref="LayoutException">The JSON is malformed or describes something that cannot be drawn.</exception>
    public static LayoutProject Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (json.Length > MaxJsonLength)
        {
            throw new LayoutException("layout.tooLarge");
        }

        LayoutProject? project;

        try
        {
            project = JsonSerializer.Deserialize<LayoutProject>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new LayoutException("layout.malformed", exception);
        }

        if (project is null)
        {
            throw new LayoutException("layout.malformed");
        }

        Validate(project);

        return project;
    }

    public static string Serialize(LayoutProject project)
    {
        Validate(project);

        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>Checks that a layout can be drawn.</summary>
    /// <exception cref="LayoutException">It cannot; the message says why.</exception>
    public static void Validate(LayoutProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (project.Version != LayoutProject.CurrentVersion)
        {
            throw new LayoutException("layout.version");
        }

        if (!Enum.IsDefined(project.Paper) || !Enum.IsDefined(project.Orientation))
        {
            throw new LayoutException("layout.paper");
        }

        if (!double.IsFinite(project.MarginMm) || project.MarginMm < 0 || project.MarginMm > MaxMarginMm)
        {
            throw new LayoutException("layout.margin");
        }

        if (project.Pages is null || project.Pages.Count == 0 || project.Pages.Count > MaxPages)
        {
            throw new LayoutException("layout.pageCount");
        }

        var (pageWidth, pageHeight) = project.PageSizeMm();

        foreach (var page in project.Pages)
        {
            if (page?.Elements is null || page.Elements.Count == 0)
            {
                throw new LayoutException("layout.emptyPage");
            }

            if (page.Elements.Count > MaxElementsPerPage)
            {
                throw new LayoutException("layout.elementCount");
            }

            foreach (var element in page.Elements)
            {
                ValidateElement(element, pageWidth, pageHeight);
            }
        }
    }

    private static void ValidateElement(LayoutElement? element, double pageWidth, double pageHeight)
    {
        if (element is null || string.IsNullOrWhiteSpace(element.Image) || element.Image.Length > MaxImageKeyLength)
        {
            throw new LayoutException("layout.image");
        }

        if (!double.IsFinite(element.XMm) || !double.IsFinite(element.YMm) || !double.IsFinite(element.RotationDeg))
        {
            throw new LayoutException("layout.position");
        }

        if (!double.IsFinite(element.WidthMm) || !double.IsFinite(element.HeightMm)
            || element.WidthMm < MinSideMm || element.HeightMm < MinSideMm
            || element.WidthMm > MaxSideMm || element.HeightMm > MaxSideMm)
        {
            throw new LayoutException("layout.size");
        }

        // A picture whose centre is off the paper would print nothing or a
        // sliver; it is a mistake, not a choice.
        var centreX = element.XMm + (element.WidthMm / 2);
        var centreY = element.YMm + (element.HeightMm / 2);

        if (centreX < 0 || centreY < 0 || centreX > pageWidth || centreY > pageHeight)
        {
            throw new LayoutException("layout.offPage");
        }
    }
}
