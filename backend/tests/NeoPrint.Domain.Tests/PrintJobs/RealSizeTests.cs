using NeoPrint.Domain.Files;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Domain.Tests.PrintJobs;

/// <summary>
/// Scans and layouts print at 100%, on their own paper, one page to a sheet
/// (plan section 2.11: "Real ölçü 100%" and the layout's paper are compulsory).
/// </summary>
public sealed class RealSizeTests
{
    private static readonly PrintOptions RealSizeA4 = PrintOptions.Default with { Scaling = PageScaling.Actual };

    [Fact]
    public void Actual_size_on_the_layout_paper_one_to_a_sheet_is_fine()
    {
        RealSizeA4.RealSizeProblems("A4").Should().BeEmpty();
        (RealSizeA4 with { Copies = 3, Color = true, Duplex = true }).RealSizeProblems("A4").Should().BeEmpty();
    }

    [Fact]
    public void Fitting_another_paper_or_n_up_is_each_named()
    {
        var problems = (PrintOptions.Default with { Paper = "A3", PagesPerSheet = 2 }).RealSizeProblems("A4");

        problems.Select(problem => problem.Field).Should().Equal("scaling", "paper", "pagesPerSheet");
        problems[1].Message.Arguments.Should().Contain(new KeyValuePair<string, string>("paper", "A4"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("A4", "A4")]
    [InlineData("A3", "A3")]
    [InlineData("A5", "A4")]
    public void An_a5_layout_prints_on_a4_and_other_files_are_free(string? layoutPaper, string? printPaper)
    {
        new StoredFile { LayoutPaper = layoutPaper }.RealSizePaper.Should().Be(printPaper);
    }
}
