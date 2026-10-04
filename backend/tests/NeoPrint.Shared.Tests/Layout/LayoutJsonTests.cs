using NeoPrint.Layout;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>Layout JSON comes from phones and kiosks: it is checked before anything is drawn.</summary>
public sealed class LayoutJsonTests
{
    private const string Card = """
        {"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,
         "pages":[{"elements":[{"image":"front","xMm":62.2,"yMm":89.52,"widthMm":85.6,"heightMm":53.98,"rotationDeg":0}]}]}
        """;

    [Fact]
    public void The_json_the_editors_write_is_read_back_unchanged()
    {
        var project = LayoutJson.Parse(Card);

        project.Paper.Should().Be(LayoutPaper.A4);
        project.Pages.Should().ContainSingle();
        project.Pages[0].Elements.Should().ContainSingle()
            .Which.Should().Be(new LayoutElement("front", 62.2, 89.52, 85.6, 53.98));

        LayoutJson.Parse(LayoutJson.Serialize(project)).Should().BeEquivalentTo(project);
    }

    [Theory]
    [InlineData("{", "layout.malformed")]
    [InlineData("null", "layout.malformed")]
    [InlineData("""{"version":2,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"a","xMm":0,"yMm":0,"widthMm":10,"heightMm":10}]}]}""", "layout.version")]
    [InlineData("""{"version":1,"paper":"Letter","orientation":"Portrait","marginMm":10,"pages":[]}""", "layout.malformed")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":-1,"pages":[{"elements":[{"image":"a","xMm":0,"yMm":0,"widthMm":10,"heightMm":10}]}]}""", "layout.margin")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[]}""", "layout.pageCount")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[]}]}""", "layout.emptyPage")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"","xMm":0,"yMm":0,"widthMm":10,"heightMm":10}]}]}""", "layout.image")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"a","xMm":0,"yMm":0,"widthMm":0,"heightMm":10}]}]}""", "layout.size")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"a","xMm":0,"yMm":0,"widthMm":2000,"heightMm":10}]}]}""", "layout.size")]
    [InlineData("""{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"a","xMm":300,"yMm":0,"widthMm":10,"heightMm":10}]}]}""", "layout.offPage")]
    public void A_layout_that_cannot_be_drawn_is_refused_with_a_reason(string json, string key)
    {
        var parse = () => LayoutJson.Parse(json);

        parse.Should().Throw<LayoutException>().WithMessage(key);
    }

    [Fact]
    public void Not_a_number_is_refused()
    {
        var project = new LayoutProject(
            LayoutProject.CurrentVersion,
            LayoutPaper.A4,
            LayoutOrientation.Portrait,
            10,
            [new LayoutPage([new LayoutElement("a", double.NaN, 0, 10, 10)])]);

        var validate = () => LayoutJson.Validate(project);

        validate.Should().Throw<LayoutException>().WithMessage("layout.position");
    }

    [Fact]
    public void The_page_and_picture_counts_are_capped()
    {
        var element = new LayoutElement("a", 10, 10, 10, 10);
        var tooManyPages = new LayoutProject(
            LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, 10,
            [.. Enumerable.Repeat(new LayoutPage([element]), LayoutJson.MaxPages + 1)]);
        var crowded = new LayoutProject(
            LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, 10,
            [new LayoutPage([.. Enumerable.Repeat(element, LayoutJson.MaxElementsPerPage + 1)])]);

        ((Action)(() => LayoutJson.Validate(tooManyPages))).Should().Throw<LayoutException>().WithMessage("layout.pageCount");
        ((Action)(() => LayoutJson.Validate(crowded))).Should().Throw<LayoutException>().WithMessage("layout.elementCount");
    }

    [Fact]
    public void Oversized_json_is_refused_before_it_is_parsed()
    {
        var parse = () => LayoutJson.Parse(new string(' ', LayoutJson.MaxJsonLength + 1));

        parse.Should().Throw<LayoutException>().WithMessage("layout.tooLarge");
    }

    [Fact]
    public void A_card_near_the_edge_leaves_the_area_a_printer_can_reach()
    {
        var project = LayoutJson.Parse(Card);
        var inside = new LayoutElement("a", 4, 4, 85.6, 53.98);
        var over = new LayoutElement("a", 3, 4, 85.6, 53.98);

        // Turned 90°, the card reaches 15.81 mm further left than its box.
        var turned = new LayoutElement("a", 10, 40, 85.6, 53.98, 90);

        LayoutGeometry.LeavesPrintableArea(project, inside).Should().BeFalse();
        LayoutGeometry.LeavesPrintableArea(project, over).Should().BeTrue();
        LayoutGeometry.LeavesPrintableArea(project, turned).Should().BeFalse();
        LayoutGeometry.Bounds(turned).Left.Should().BeApproximately(25.81, 0.001);
        LayoutGeometry.LeavesPrintableArea(project, inside, printerMarginMm: 5).Should().BeTrue();
    }
}
