using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;
using NeoPrint.Kiosk.ViewModels;
using NeoPrint.Layout;

using SkiaSharp;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Screen 4a, "Səhifədə yerləşdir" (plan section 2.11): a flash drive's photo
/// placed at a real size, printed at 100% on its paper.
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class LayoutFlowTests : IDisposable
{
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "neoprint-drive", Guid.NewGuid().ToString("N"));
    private readonly List<OrderTestKit> _kits = [];

    public LayoutFlowTests() => Directory.CreateDirectory(_drive);

    public void Dispose()
    {
        Directory.Delete(_drive, recursive: true);

        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private static string T(string key, params object?[] arguments) => Strings.Get("az", key, arguments);

    // A 1600 × 1000 photo of one dark colour, so the printed card can be measured.
    private async Task<(PrintFlowViewModel Flow, OrderTestKit Kit)> PhotoOpenAsync()
    {
        var kit = await OrderTestKit.CreateAsync();
        _kits.Add(kit);

        using (var bitmap = new SKBitmap(1600, 1000))
        {
            bitmap.Erase(new SKColor(20, 40, 120));
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(_drive, "photo.png"), data.ToArray());
        }

        var drives = new FakeRemovableDriveWatcher();
        drives.Drives.Add(new RemovableDrive(_drive, "USB", 1, 1));

        var flow = new PrintFlowViewModel(kit.Orders, drives, pageImage: _ => null) { Language = "az" };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());

        return (flow, kit);
    }

    [Fact]
    public async Task A_photo_goes_on_the_paper_as_large_as_fits_and_takes_a_card_size()
    {
        var (flow, _) = await PhotoOpenAsync();

        flow.CanStartLayout.Should().BeTrue();
        await flow.StartLayoutCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Layout);
        flow.LayoutPreview.Should().NotBeNull();
        var photo = flow.Layout!.Pages.Single().Elements.Single();
        (photo.WidthMm, photo.HeightMm).Should().Be((190, 118.75));

        flow.LayoutSizeCommand.Execute("IdCard");

        flow.LayoutSizeText.Should().Be("85,6 × 53,98 mm");
        flow.LayoutEdge.Should().BeNull();
        flow.LayoutOffPage.Should().BeNull();
    }

    [Fact]
    public async Task Eight_cards_print_at_the_real_size_locked_to_A4_at_100_percent()
    {
        var (flow, kit) = await PhotoOpenAsync();
        await flow.StartLayoutCommand.ExecuteAsync(null);

        flow.LayoutApplyTemplateCommand.Execute("Cards");
        flow.Layout!.Pages[0].Elements.Should().HaveCount(8)
            .And.OnlyContain(card => card.WidthMm == 85.6 && card.HeightMm == 53.98);

        await flow.LayoutDoneCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Preview);
        flow.PageCount.Should().Be(1);
        flow.CanEditFile.Should().BeFalse("the image editor would undo the layout");
        flow.RealSizePaper.Should().Be("A4");

        flow.ContinueCommand.Execute(null);
        flow.ChoosePaperCommand.Execute("A3");
        flow.ChoosePerSheetCommand.Execute("4");

        flow.Paper.Should().Be("A4");
        flow.PagesPerSheet.Should().Be(1);
        flow.RealSizeNote.Should().Be(T("options.realSize", "A4"));

        await flow.PlaceCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Placed);
        kit.Journal.All().Single().Options.Scaling.Should().Be("Actual");
    }

    [Fact]
    public void The_kiosk_draws_a_card_the_size_the_backend_does()
    {
        if (!PageRenderer.IsAvailable)
        {
            return;
        }

        using var bitmap = new SKBitmap(1600, 1000);
        bitmap.Erase(new SKColor(20, 40, 120));
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);

        var card = new LayoutElement(KioskLayouts.ImageKey, 20, 30, 85.6, 53.98);
        var project = new LayoutProject(
            LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, 10, [new LayoutPage([card])]);
        var pdf = Path.Combine(_drive, "layout.pdf");

        KioskLayouts.Render(project, data.ToArray(), pdf);

        // 10 pixels to the millimetre: the card is 856 × 540 pixels, give or take one.
        var page = PageRenderer.Render(File.ReadAllBytes(pdf), 0, 2100);
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, -1, -1);

        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                if (page.Pixels[(y * page.Stride) + (x * 4)] < 200)
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            }
        }

        page.Height.Should().BeInRange(2969, 2971);
        (right - left + 1).Should().BeInRange(855, 857);
        (bottom - top + 1).Should().BeInRange(539, 541);
        left.Should().BeInRange(199, 201);
        top.Should().BeInRange(299, 301);
    }

    [Fact]
    public async Task A_picture_off_the_paper_holds_back_done_and_cancel_changes_nothing()
    {
        var (flow, _) = await PhotoOpenAsync();
        await flow.StartLayoutCommand.ExecuteAsync(null);
        flow.LayoutSizeCommand.Execute("IdCard");
        var card = flow.LayoutSelectedElement!;

        flow.LayoutDrag(card, 300, 0);

        flow.LayoutOffPage.Should().Be(T("layout.offPage"));
        flow.CanFinishLayout.Should().BeFalse();
        await flow.LayoutDoneCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.Layout);

        flow.LayoutDrag(card, 0, 0);
        flow.LayoutSmallerCommand.Execute(null);
        flow.LayoutEdge.Should().BeNull();

        flow.LayoutCancelCommand.Execute(null);

        flow.Stage.Should().Be(FlowStage.Preview);
        flow.RealSizePaper.Should().BeNull();
        flow.CanEditFile.Should().BeTrue();
        flow.Layout.Should().BeNull();
    }

    [Fact]
    public async Task A_touch_selects_the_picture_under_it_and_snaps_it_to_the_margin()
    {
        var (flow, _) = await PhotoOpenAsync();
        await flow.StartLayoutCommand.ExecuteAsync(null);
        flow.LayoutSizeCommand.Execute("IdCard");
        flow.LayoutDuplicateCommand.Execute(null);
        flow.Layout!.Pages[0].Elements.Should().HaveCount(2);

        flow.LayoutSelectAt(5, 5);
        flow.HasLayoutSelection.Should().BeFalse();

        var copy = flow.Layout.Pages[0].Elements[1];
        flow.LayoutSelectAt(copy.XMm + 80, copy.YMm + 50);
        flow.LayoutSelected.Should().Be(1, "the copy lies on top");

        flow.LayoutDrag(copy, 11 - copy.XMm, 0);

        flow.LayoutSelectedElement!.XMm.Should().Be(10);
        flow.LayoutLines.Should().Contain(new SnapLine(true, 10));

        flow.LayoutGestureEnded();
        flow.LayoutLines.Should().BeEmpty();
    }
}
