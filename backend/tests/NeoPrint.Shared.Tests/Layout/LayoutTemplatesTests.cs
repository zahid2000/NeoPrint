using System.Text.Json;
using System.Text.Json.Serialization;

using NeoPrint.Layout;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>
/// The ready-made arrangements (plan section 2.11). Pictures keep their real
/// size; only a block that would not fit is reduced, uniformly.
/// </summary>
public sealed class LayoutTemplatesTests
{
    private static readonly TemplatePicture Front = new("front", 85.6, 53.98);
    private static readonly TemplatePicture Back = new("back", 85.6, 53.98);

    [Fact]
    public void Front_and_back_sit_one_above_the_other_in_the_middle_of_a4_at_real_size()
    {
        var project = LayoutTemplates.Apply(LayoutTemplate.IdFrontBack, [Front, Back], 10);

        project.Paper.Should().Be(LayoutPaper.A4);
        project.Orientation.Should().Be(LayoutOrientation.Portrait);

        // Block: 85.6 wide, 53.98 + 10 + 53.98 = 117.96 tall.
        // Left (210 - 85.6) / 2 = 62.2; top (297 - 117.96) / 2 = 89.52.
        project.Pages.Should().ContainSingle().Which.Elements.Should().Equal(
            new LayoutElement("front", 62.2, 89.52, 85.6, 53.98),
            new LayoutElement("back", 62.2, 153.5, 85.6, 53.98));
    }

    [Fact]
    public void One_side_alone_is_placed_once()
    {
        var project = LayoutTemplates.Apply(LayoutTemplate.IdFrontBack, [Front], 10);

        project.Pages[0].Elements.Should().Equal(new LayoutElement("front", 62.2, 121.51, 85.6, 53.98));
    }

    [Fact]
    public void Eight_cards_fill_two_columns_of_four()
    {
        var project = LayoutTemplates.Apply(LayoutTemplate.Cards2x4, [Front], 10);
        var elements = project.Pages[0].Elements;

        // Block: 2 × 85.6 + 5 = 176.2 wide, 4 × 53.98 + 15 = 230.92 tall.
        // Left (210 - 176.2) / 2 = 16.9; top (297 - 230.92) / 2 = 33.04.
        elements.Should().HaveCount(8).And.OnlyContain(element => element.Image == "front");
        elements[0].Should().Be(new LayoutElement("front", 16.9, 33.04, 85.6, 53.98));
        elements[1].Should().Be(new LayoutElement("front", 107.5, 33.04, 85.6, 53.98));
        elements[7].Should().Be(new LayoutElement("front", 107.5, 209.98, 85.6, 53.98));
    }

    [Fact]
    public void Six_photos_go_round_the_pictures_given()
    {
        var project = LayoutTemplates.Apply(
            LayoutTemplate.Photo3x4x6, [new("a", 30, 40), new("b", 30, 40)], 10);

        project.Pages[0].Elements.Select(element => element.Image).Should().Equal("a", "b", "a", "b", "a", "b");
    }

    [Fact]
    public void A_block_too_big_for_the_margins_is_reduced_uniformly_and_stays_inside()
    {
        var a5 = new TemplatePicture("page", 148, 210);

        var project = LayoutTemplates.Apply(LayoutTemplate.Grid2x2, [a5], 10);

        foreach (var element in project.Pages[0].Elements)
        {
            (element.WidthMm / element.HeightMm).Should().BeApproximately(148.0 / 210, 0.001);
            element.XMm.Should().BeGreaterThanOrEqualTo(10);
            element.YMm.Should().BeGreaterThanOrEqualTo(10);
            (element.XMm + element.WidthMm).Should().BeLessThanOrEqualTo(200.01);
            (element.YMm + element.HeightMm).Should().BeLessThanOrEqualTo(287.01);
        }
    }

    [Fact]
    public void A_scan_gets_one_page_per_picture_by_default()
    {
        var project = LayoutTemplates.OnePerPage([Front, new("page", 210, 297)], 10);

        project.Pages.Should().HaveCount(2);
        project.Pages[0].Elements.Should().Equal(new LayoutElement("front", 62.2, 121.51, 85.6, 53.98));

        // A full A4 page is reduced to fit the 190 × 277 inside the margins:
        // by 190 / 210, the tighter of the two.
        var page = project.Pages[1].Elements.Single();
        page.WidthMm.Should().Be(190);
        page.HeightMm.Should().Be(268.71);
    }

    [Fact]
    public void Nothing_to_arrange_is_refused()
    {
        var apply = () => LayoutTemplates.Apply(LayoutTemplate.IdFrontBack, [], 10);

        apply.Should().Throw<LayoutException>().WithMessage("layout.noPictures");
    }

    [Fact]
    public void Every_template_result_is_a_valid_layout()
    {
        foreach (var template in Enum.GetValues<LayoutTemplate>())
        {
            var project = LayoutTemplates.Apply(template, [Front, Back], 10);

            ((Action)(() => LayoutJson.Validate(project))).Should().NotThrow(template.ToString());
        }
    }

    /// <summary>
    /// The mobile app builds the same arrangements in Dart and checks itself
    /// against this file, so a phone and a kiosk lay a card out identically.
    /// Set <c>NEOPRINT_UPDATE_FIXTURES=1</c> to rewrite it after a deliberate change.
    /// </summary>
    [Fact]
    public void The_shared_fixture_matches_the_templates()
    {
        var path = FixturePath();
        var cases = Cases().Select(item => new FixtureCase(item.Name, item.Template, item.MarginMm, item.Pictures, Build(item))).ToList();

        if (Environment.GetEnvironmentVariable("NEOPRINT_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cases, FixtureOptions) + "\n");
        }

        var stored = JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(path), FixtureOptions);

        stored.Should().BeEquivalentTo(cases, options => options.WithStrictOrdering());
    }

    private static readonly JsonSerializerOptions FixtureOptions = new(LayoutJson.Options) { WriteIndented = true };

    /// <param name="Template">A <see cref="LayoutTemplate"/> name, or "OnePerPage".</param>
    private sealed record FixtureCase(
        string Name, string Template, double MarginMm, IReadOnlyList<TemplatePicture> Pictures, LayoutProject Expected);

    private static IEnumerable<(string Name, string Template, double MarginMm, IReadOnlyList<TemplatePicture> Pictures)> Cases()
    {
        TemplatePicture[] cards = [Front, Back];
        TemplatePicture[] photo = [new("photo", 30, 40)];
        TemplatePicture[] passport = [new("passport", 125, 88)];
        TemplatePicture[] pages = [new("p1", 210, 297), new("p2", 148, 210)];

        yield return ("id-front-back", nameof(LayoutTemplate.IdFrontBack), 10, cards);
        yield return ("id-front-only", nameof(LayoutTemplate.IdFrontBack), 10, [Front]);
        yield return ("grid-2x2-a5-reduced", nameof(LayoutTemplate.Grid2x2), 10, [pages[1]]);
        yield return ("cards-2x4", nameof(LayoutTemplate.Cards2x4), 10, cards);
        yield return ("photo-3x4-x6", nameof(LayoutTemplate.Photo3x4x6), 10, photo);
        yield return ("passport-centered", nameof(LayoutTemplate.PassportCentered), 10, passport);
        yield return ("one-per-page-margin-5", "OnePerPage", 5, [Front, .. pages]);
    }

    private static LayoutProject Build((string Name, string Template, double MarginMm, IReadOnlyList<TemplatePicture> Pictures) item)
        => item.Template == "OnePerPage"
            ? LayoutTemplates.OnePerPage(item.Pictures, item.MarginMm)
            : LayoutTemplates.Apply(Enum.Parse<LayoutTemplate>(item.Template), item.Pictures, item.MarginMm);

    private static string FixturePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "tests", "fixtures", "layout", "templates.json");

            if (Directory.Exists(Path.Combine(directory.FullName, "backend", "tests")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
