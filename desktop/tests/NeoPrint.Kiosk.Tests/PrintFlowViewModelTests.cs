using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The flash order and the cancel screen as the customer walks them
/// (plan section 7.3, screens 2–5 and 11).
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class PrintFlowViewModelTests : IDisposable
{
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "neoprint-drive", Guid.NewGuid().ToString("N"));
    private readonly List<OrderTestKit> _kits = [];

    public PrintFlowViewModelTests() => Directory.CreateDirectory(_drive);

    public void Dispose()
    {
        Directory.Delete(_drive, recursive: true);

        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private async Task<(PrintFlowViewModel Flow, OrderTestKit Kit)> FlowAsync(
        IReadOnlyList<string>? allowedFormats = null, int maxFileSizeMb = 50)
    {
        var kit = await OrderTestKit.CreateAsync(allowedFormats: allowedFormats, maxFileSizeMb: maxFileSizeMb);
        _kits.Add(kit);

        var drives = new FakeRemovableDriveWatcher();
        drives.Drives.Add(new RemovableDrive(_drive, "USB", 1, 1));

        // Pages are drawn (PDFium ships with the build); a bitmap needs Avalonia's renderer, which is not started here.
        return (new PrintFlowViewModel(kit.Orders, drives, pageImage: _ => null) { Language = "az" }, kit);
    }

    private static string T(string key, params object?[] arguments) => Strings.Get("az", key, arguments);

    [Fact]
    public async Task The_drive_lists_what_the_kiosk_takes_and_says_why_a_file_cannot_be_chosen()
    {
        Directory.CreateDirectory(Path.Combine(_drive, "Dərslər"));
        Directory.CreateDirectory(Path.Combine(_drive, "System Volume Information"));
        await File.WriteAllTextAsync(Path.Combine(_drive, "notes.txt"), "salam");
        await File.WriteAllTextAsync(Path.Combine(_drive, "photo.png"), "png");
        await File.WriteAllTextAsync(Path.Combine(_drive, "setup.exe"), "MZ");
        await File.WriteAllTextAsync(Path.Combine(_drive, ".hidden.pdf"), "%PDF-");
        await File.WriteAllBytesAsync(Path.Combine(_drive, "big.pdf"), new byte[(1024 * 1024) + 1]);

        var (flow, kit) = await FlowAsync(allowedFormats: ["pdf", "txt"], maxFileSizeMb: 1);
        kit.Pdf(2, _drive, "report.pdf");

        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));

        flow.Stage.Should().Be(FlowStage.Browse);
        flow.Entries.Select(entry => (entry.Name, entry.IsSelectable)).Should().Equal(
            ("Dərslər", true),
            ("big.pdf", false),
            ("notes.txt", true),
            ("photo.png", false),
            ("report.pdf", true));
        flow.Entries.Single(entry => entry.Name == "big.pdf").Reason.Should().Be(T("browse.tooLarge", 1L));
        flow.Entries.Single(entry => entry.Name == "photo.png").Reason.Should().Be(T("browse.notAllowed"));
    }

    [Fact]
    public async Task A_pdf_goes_from_the_preview_to_a_price_and_into_the_queue()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(3, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));

        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());

        flow.Stage.Should().Be(FlowStage.Preview);
        flow.PageCount.Should().Be(3);
        flow.Pages.Should().HaveCount(3);

        flow.ContinueCommand.Execute(null);
        flow.MoreCopiesCommand.Execute(null);

        flow.Stage.Should().Be(FlowStage.Options);
        flow.PriceText.Should().Be(Formats.Money(0.60m, "az"));
        flow.CanPlace.Should().BeTrue();

        await flow.PlaceCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Placed);
        flow.PlacedCode.Should().Be("482913");
        kit.Journal.All().Should().ContainSingle(entry => entry.State == JournalState.Queued);
        Directory.GetFileSystemEntries(kit.Spool.WorkFolder).Should().BeEmpty("the working copy goes once the order is in the spool");
    }

    [Fact]
    public async Task The_step_indicator_follows_the_order_and_shows_no_payment_step_yet()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(1, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));

        string States() => string.Join(' ', flow.Steps.Select(step => step.State.ToString()[0]));

        flow.ShowSteps.Should().BeTrue();
        flow.Steps.Select(step => step.Label).Should().Equal(T("steps.file"), T("steps.preview"), T("steps.options"), T("steps.done"));
        States().Should().Be("C U U U");

        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        States().Should().Be("D C U U");

        flow.ContinueCommand.Execute(null);
        States().Should().Be("D D C U");

        await flow.PlaceCommand.ExecuteAsync(null);
        States().Should().Be("D D D C");

        flow.StartCancel();
        flow.ShowSteps.Should().BeFalse("cancelling is not part of an order");
    }

    [Fact]
    public async Task A_range_outside_the_document_says_so_and_cannot_be_placed()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(3, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);

        flow.ChooseRangeCommand.Execute(null);

        // Half typed is not an error yet: nothing moves under the finger.
        foreach (var typed in new[] { "2-", "2,", "1,2-" })
        {
            flow.RangeText = typed;

            flow.Error.Should().BeNull();
            flow.RangeProblem.Should().BeNull(typed);
            flow.QuoteDetail.Should().Be(T("options.rangeNeeded"));
        }

        flow.RangeText = "2-7";

        flow.CanPlace.Should().BeFalse();
        flow.PriceText.Should().BeNull();
        flow.Error.Should().BeNull("the banner is for the order, the range says what is wrong with it");
        flow.RangeProblem.Should().Be(T("options.rangeInvalid", 3));

        flow.RangeText = "2-3";
        flow.RangeProblem.Should().BeNull();
        flow.CanPlace.Should().BeTrue();
    }

    [Fact]
    public async Task An_answer_lost_on_the_way_back_is_retried_with_the_same_key_and_makes_one_job()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(2, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);

        kit.Backend.DropNextOrder = true;
        await flow.PlaceCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Options);
        flow.Error.Should().Be(T("error.network"));

        await flow.PlaceCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.Placed);
        kit.Backend.IdempotencyKeys.Should().HaveCount(2).And.OnlyContain(key => key == kit.Backend.IdempotencyKeys[0]);
    }

    [Fact]
    public async Task Changed_options_after_a_failure_are_a_new_order()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(2, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);

        kit.Backend.DropNextOrder = true;
        await flow.PlaceCommand.ExecuteAsync(null);
        flow.MoreCopiesCommand.Execute(null);
        await flow.PlaceCommand.ExecuteAsync(null);

        kit.Backend.IdempotencyKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Pulling_the_drive_out_drops_what_was_not_ordered()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(2, _drive, "report.pdf");
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        var finished = 0;
        flow.Finished += () => finished++;

        flow.DriveRemoved();

        finished.Should().Be(1);
        flow.Stage.Should().Be(FlowStage.Browse);
        flow.Pages.Should().BeEmpty();
        Directory.GetFileSystemEntries(kit.Spool.WorkFolder).Should().BeEmpty();
    }

    [Fact]
    public async Task An_order_left_half_way_is_dropped_and_the_language_goes_back_for_the_next_person()
    {
        var (flow, kit) = await FlowAsync();
        kit.Pdf(2, _drive, "report.pdf");
        var shell = new MainWindowViewModel { Flow = flow, Screen = KioskScreen.Flow, Language = "ru" };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());

        try
        {
            shell.ResetIfIdle(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), "az").Should().BeFalse("a minute is not long enough");
            flow.Stage.Should().Be(FlowStage.Preview);

            shell.ResetIfIdle(TimeSpan.FromSeconds(91), TimeSpan.FromSeconds(90), "az").Should().BeTrue();

            shell.Screen.Should().Be(KioskScreen.Home);
            flow.Pages.Should().BeEmpty();
            Directory.GetFileSystemEntries(kit.Spool.WorkFolder).Should().BeEmpty("the copy of the customer's file goes with the order");
            shell.Language.Should().Be("az");
        }
        finally
        {
            shell.Language = "az";
        }
    }

    [Fact]
    public async Task An_order_being_placed_is_not_interrupted_by_the_idle_reset()
    {
        var (flow, _) = await FlowAsync();
        var shell = new MainWindowViewModel { Flow = flow, Screen = KioskScreen.Flow };
        flow.Stage = FlowStage.Working;

        shell.ResetIfIdle(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(90), "az").Should().BeFalse();

        shell.Screen.Should().Be(KioskScreen.Flow);
    }

    [Fact]
    public async Task An_order_is_cancelled_by_its_code()
    {
        var (flow, kit) = await FlowAsync();
        kit.Backend.JobsByCode["123456"] = "Queued";
        flow.StartCancel();

        foreach (var digit in "1234567")
        {
            flow.DigitCommand.Execute(digit.ToString());
        }

        flow.CodeInput.Should().Be("123456", "a code has six digits");

        await flow.FindCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.CancelConfirm);

        await flow.ConfirmCancelCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.CancelDone);
        kit.Backend.Cancelled.Should().ContainSingle();
    }

    [Fact]
    public async Task A_code_that_is_unknown_or_already_printing_is_explained_not_cancelled()
    {
        var (flow, kit) = await FlowAsync();
        kit.Backend.JobsByCode["222222"] = "Printing";
        flow.StartCancel();

        flow.CodeInput = "999999";
        await flow.FindCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.CancelLookup);
        flow.Error.Should().Be(T("cancel.notFound"));

        flow.CodeInput = "222222";
        await flow.FindCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.CancelLookup);
        flow.Error.Should().Be(T("cancel.cannot", T("jobStatus.Printing")));
        kit.Backend.Cancelled.Should().BeEmpty();
    }
}
