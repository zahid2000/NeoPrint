using NeoPrint.Layout;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>
/// The editor's operations in C#, for the kiosk - the same numbers the mobile
/// app's Dart tests check (<c>mobile/test/layout_editing_test.dart</c>).
/// </summary>
public sealed class LayoutEditingTests
{
    private static readonly LayoutElement Card = new("card", 50, 50, 85.6, 53.98);

    private static readonly LayoutProject Project = new(
        LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, 10, [new LayoutPage([Card])]);

    [Fact]
    public void A_picture_dragged_near_the_margin_snaps_onto_it()
    {
        var (moved, lines) = LayoutEditing.Move(Project, 0, 0, Card, -38.5, 0);

        moved.XMm.Should().Be(10);
        lines.Should().Contain(new SnapLine(true, 10));
    }

    [Fact]
    public void A_picture_dragged_near_the_middle_centres_on_the_page()
    {
        var (moved, lines) = LayoutEditing.Move(Project, 0, 0, Card, 13.5, 0);

        (moved.XMm + (moved.WidthMm / 2)).Should().BeApproximately(105, 0.001);
        lines.Should().Contain(new SnapLine(true, 105));
    }

    [Fact]
    public void Far_from_anything_a_move_is_exact()
    {
        var (moved, lines) = LayoutEditing.Move(Project, 0, 0, Card, 7.3, 11.1);

        moved.XMm.Should().Be(57.3);
        moved.YMm.Should().Be(61.1);
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Scaling_keeps_the_shape_about_the_centre_and_never_goes_under_1_mm()
    {
        var bigger = LayoutEditing.Scale(Card, 2);
        var tiny = LayoutEditing.Scale(Card, 0.001);

        bigger.WidthMm.Should().Be(171.2);
        bigger.HeightMm.Should().Be(107.96);
        (bigger.XMm + (bigger.WidthMm / 2)).Should().BeApproximately(92.8, 0.01);
        tiny.HeightMm.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void A_size_is_given_the_way_the_picture_is_held()
    {
        var upright = Card with { WidthMm = 40, HeightMm = 60 };

        var landscape = LayoutEditing.Resize(Card with { WidthMm = 171.2, HeightMm = 107.96 }, 85.6, 53.98);
        var portrait = LayoutEditing.Resize(upright, 85.6, 53.98);

        (landscape.WidthMm, landscape.HeightMm).Should().Be((85.6, 53.98));
        (portrait.WidthMm, portrait.HeightMm).Should().Be((53.98, 85.6));
    }

    [Fact]
    public void Turn_duplicate_delete_and_centre()
    {
        LayoutEditing.Rotate90(Card with { RotationDeg = 270 }).RotationDeg.Should().Be(0);
        LayoutEditing.Rotate90(Card with { RotationDeg = 45 }).RotationDeg.Should().Be(90);

        var two = LayoutEditing.Duplicate(Project, 0, 0);
        two.Pages[0].Elements.Should().HaveCount(2);
        two.Pages[0].Elements[1].XMm.Should().Be(55);
        LayoutEditing.Delete(two, 0, 0).Pages[0].Elements.Single().XMm.Should().Be(55);

        var centred = LayoutEditing.Centre(Project, Card);
        (centred.XMm, centred.YMm).Should().Be((62.2, 121.51));
    }

    [Fact]
    public void A_photo_goes_in_as_large_as_fits_and_off_the_paper_is_caught()
    {
        var empty = Project with { Pages = [new LayoutPage([])] };

        var photo = LayoutEditing.Add(empty, 0, "photo", 0.75).Pages[0].Elements.Single();

        photo.WidthMm.Should().Be(190);
        photo.HeightMm.Should().BeApproximately(253.33, 0.01);
        LayoutEditing.HasOffPage(LayoutEditing.Replace(Project, 0, 0, Card with { XMm = 200 })).Should().BeTrue();
        LayoutEditing.LeavesPrintableArea(LayoutEditing.Replace(Project, 0, 0, Card with { XMm = 2 })).Should().BeTrue();
    }
}
