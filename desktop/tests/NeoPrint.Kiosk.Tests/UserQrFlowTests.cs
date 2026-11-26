using System.Text.Json;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Printing with the personal QR at the kiosk (plan section 9.4; screen 10,
/// UserQrFiles; docs/DECISIONS.md T-144): the scanner's typing told from a
/// person's, the user's files listed, an unpaid one ordered with the app's
/// choices, one paid in the app sent to the queue, and one paid elsewhere
/// only shown.
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class UserQrFlowTests : IDisposable
{
    private const string Code = "NPU:0123456789abcdef0123456789abcdef:1790000000:c2lnbmF0dXJl";

    private readonly List<OrderTestKit> _kits = [];

    public void Dispose()
    {
        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private static string T(string key, params object?[] arguments) => Strings.Get("az", key, arguments);

    private async Task<(PrintFlowViewModel Flow, OrderTestKit Kit)> FlowAsync(bool canColor = true)
    {
        var kit = await OrderTestKit.CreateAsync(skipPayment: false, canColor: canColor);
        _kits.Add(kit);

        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), pageImage: _ => null)
        {
            Language = "az",
            Delay = (_, token) => Task.Delay(5, token),
        };

        return (flow, kit);
    }

    private static UserQrItem Prepared(string name, int pages, PreparedPreset? preset = null)
        => new("Prepared", Guid.NewGuid(), name, pages, preset, null, null, null, true, null, DateTime.UtcNow);

    private static UserQrItem Paid(string name, bool here, string kiosk = "KSK-0001")
        => new("Paid", Guid.NewGuid(), name, 3, null, new JobOptions(), 0.30m, "AZN", here, kiosk, DateTime.UtcNow);

    [Fact]
    public void The_scanner_s_line_is_told_from_a_person_s_typing()
    {
        var buffer = new QrScannerBuffer();
        var at = TimeSpan.FromSeconds(10);

        foreach (var character in Code)
        {
            at += TimeSpan.FromMilliseconds(8);
            buffer.Accept(character, at);
        }

        buffer.Enter(at + TimeSpan.FromMilliseconds(8)).Should().Be(Code);

        // A person: the same characters, slowly - each one starts a new line.
        foreach (var character in Code)
        {
            at += TimeSpan.FromMilliseconds(180);
            buffer.Accept(character, at);
        }

        buffer.Enter(at + TimeSpan.FromMilliseconds(8)).Should().BeNull();

        // Too short to be a code, or Enter long after the last character.
        foreach (var character in "NPU:1")
        {
            at += TimeSpan.FromMilliseconds(8);
            buffer.Accept(character, at);
        }

        buffer.Enter(at + TimeSpan.FromMilliseconds(8)).Should().BeNull();

        foreach (var character in Code)
        {
            at += TimeSpan.FromMilliseconds(8);
            buffer.Accept(character, at);
        }

        buffer.Enter(at + TimeSpan.FromSeconds(1)).Should().BeNull();
    }

    [Fact]
    public void Only_a_personal_qr_on_a_free_screen_opens_the_user_s_files()
    {
        var shell = new MainWindowViewModel { Screen = KioskScreen.Home, CanPrintFromFlash = true };
        var asked = new List<string>();
        shell.UserQrRequested += asked.Add;

        shell.Scanned("MOCKBANK:123456789").Should().BeFalse("a bank QR is not the kiosk's to read");
        shell.Scanned(Code).Should().BeTrue();
        asked.Should().Equal(Code);

        // Somebody is paying: their screen stays.
        shell.Screen = KioskScreen.OutOfService;
        shell.Scanned(Code).Should().BeFalse();
        asked.Should().ContainSingle();
    }

    [Fact]
    public async Task The_user_s_files_are_listed_and_one_paid_elsewhere_says_where()
    {
        var (flow, kit) = await FlowAsync();
        kit.Backend.UserQrItems.AddRange(
        [
            Paid("bilet.pdf", here: true),
            Paid("müqavilə.pdf", here: false, kiosk: "KSK-0042"),
            Prepared("hesabat.pdf", 2, new PreparedPreset(Color: false)),
        ]);

        await flow.StartUserQrAsync(Code);

        kit.Backend.ScannedCodes.Should().Equal(Code);
        flow.Stage.Should().Be(FlowStage.UserFiles);
        flow.Error.Should().BeNull();
        flow.UserFiles.Should().HaveCount(3);
        string.Join(' ', flow.Steps.Select(step => step.State.ToString()[0])).Should().Be("C U U U U");

        var here = flow.UserFiles[0];
        here.ActionText.Should().Be(T("userQr.collect"));
        here.IsPrintable.Should().BeTrue();
        here.Amount.Should().Be(Formats.Money(0.30m, "az"));

        var elsewhere = flow.UserFiles[1];
        elsewhere.IsPrintable.Should().BeFalse();
        elsewhere.Reason.Should().Be(T("userQr.paidElsewhere", "KSK-0042"));

        var prepared = flow.UserFiles[2];
        prepared.ActionText.Should().Be(T("userQr.choose"));
        prepared.Detail.Should().Be("2 səhifə · " + T("options.bw"));

        // Picking the one paid elsewhere does nothing.
        await flow.PickUserFileCommand.ExecuteAsync(elsewhere);
        kit.Backend.Released.Should().BeEmpty();
        flow.Stage.Should().Be(FlowStage.UserFiles);
    }

    [Fact]
    public async Task An_order_paid_in_the_app_goes_to_the_queue_and_a_refusal_is_said()
    {
        var (flow, kit) = await FlowAsync();
        var paid = Paid("bilet.pdf", here: true);
        kit.Backend.UserQrItems.Add(paid);
        await flow.StartUserQrAsync(Code);

        kit.Backend.ReleaseRefusal = "Bu kioskda kifayət qədər kağız yoxdur.";
        await flow.PickUserFileCommand.ExecuteAsync(flow.UserFiles.Single());
        flow.Stage.Should().Be(FlowStage.UserFiles);
        flow.Error.Should().Be("Bu kioskda kifayət qədər kağız yoxdur.");

        kit.Backend.ReleaseRefusal = null;
        await flow.PickUserFileCommand.ExecuteAsync(flow.UserFiles.Single());

        kit.Backend.Released.Should().Equal(paid.Id);
        flow.Stage.Should().Be(FlowStage.Collected);
        flow.CollectedCode.Should().Be("715204");
        flow.CollectedPosition.Should().Be(T("placed.position", 1));
    }

    [Fact]
    public async Task An_unpaid_preparation_takes_the_app_s_choices_and_says_what_this_kiosk_cannot_do()
    {
        var (flow, kit) = await FlowAsync(canColor: false);
        var prepared = Prepared("hesabat.pdf", 4, new PreparedPreset(Copies: 2, Color: true, PageRange: "1-2"));
        kit.Backend.UserQrItems.Add(prepared);
        await flow.StartUserQrAsync(Code);

        await flow.PickUserFileCommand.ExecuteAsync(flow.UserFiles.Single());

        flow.Stage.Should().Be(FlowStage.Options);
        flow.IsFromApp.Should().BeTrue();
        flow.FileName.Should().Be("hesabat.pdf");
        flow.PageCount.Should().Be(4);
        flow.Copies.Should().Be(2);
        flow.AllPages.Should().BeFalse();
        flow.RangeText.Should().Be("1-2");
        flow.Color.Should().BeFalse("this kiosk prints black and white only");
        flow.PresetNote.Should().Be(T("userQr.presetNotHere", T("userQr.missingColor")));
        flow.PriceText.Should().NotBeNull("the price is worked out here, from this kiosk's prices");
        flow.CanPlace.Should().BeTrue();

        // Back goes to the list, not to a preview this kiosk does not have.
        flow.ShowPreviewCommand.Execute(null);
        flow.Stage.Should().Be(FlowStage.UserFiles);
        await flow.PickUserFileCommand.ExecuteAsync(flow.UserFiles.Single());

        await flow.PlaceCommand.ExecuteAsync(null);

        var order = kit.Backend.PreparedOrders.Should().ContainSingle().Subject;
        order.PreparedId.Should().Be(prepared.Id);
        order.Grant.Should().Be(kit.Backend.UserQrGrant.ToString());
        using (var body = JsonDocument.Parse(order.Body))
        {
            var options = body.RootElement.GetProperty("options");
            options.GetProperty("copies").GetInt32().Should().Be(2);
            options.GetProperty("pageRange").GetString().Should().Be("1-2");
            options.GetProperty("color").GetBoolean().Should().BeFalse();
        }

        // Paid on the kiosk's own payment screens; nothing of it is in the spool.
        flow.Stage.Should().Be(FlowStage.PaymentMethod);
        kit.Journal.All().Should().BeEmpty("the queue fetches the PDF from the server once it is paid");
    }

    [Fact]
    public async Task A_refused_qr_says_why_and_nothing_prepared_says_what_to_do()
    {
        var (flow, kit) = await FlowAsync();

        kit.Backend.UserQrRefusal = "QR kodun vaxtı keçib. Tətbiqdə QR kodu yenidən açın və təkrar oxudun.";
        await flow.StartUserQrAsync(Code);
        flow.Stage.Should().Be(FlowStage.UserFiles);
        flow.Error.Should().Be(kit.Backend.UserQrRefusal);
        flow.HasUserFiles.Should().BeFalse();

        kit.Backend.UserQrRefusal = null;
        await flow.StartUserQrAsync(Code);
        flow.Error.Should().BeNull();
        flow.HasNoUserFiles.Should().BeTrue();
        flow.UserFilesEmpty.Should().Be(T("userQr.empty"));
    }

    [Fact]
    public async Task More_than_five_files_are_paged_and_the_list_closes_after_a_minute()
    {
        var (flow, kit) = await FlowAsync();
        kit.Backend.UserQrItems.AddRange(Enumerable.Range(1, 7).Select(i => Prepared($"fayl-{i}.pdf", 1)));
        await flow.StartUserQrAsync(Code);

        flow.UserFiles.Should().HaveCount(PrintFlowViewModel.UserFilesPerPage);
        flow.HasMoreUserFiles.Should().BeTrue();
        flow.NextUserPageCommand.Execute(null);
        flow.UserFiles.Select(card => card.Title).Should().Equal("fayl-6.pdf", "fayl-7.pdf");
        flow.HasEarlierUserFiles.Should().BeTrue();

        var shell = new MainWindowViewModel { Flow = flow, Screen = KioskScreen.Flow };
        shell.ResetIfIdle(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(90), "az").Should().BeFalse();
        shell.ResetIfIdle(TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(90), "az").Should().BeTrue("the user's file names do not stay on a public screen");
        shell.Screen.Should().Be(KioskScreen.Home);
        flow.UserFiles.Should().BeEmpty();
    }
}
