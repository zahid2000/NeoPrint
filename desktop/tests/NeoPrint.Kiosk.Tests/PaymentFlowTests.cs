using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Paying for a flash order at the kiosk (plan sections 7.3 screens 6–9, 9.1,
/// 9.2; docs/TASKS.md T-108): the order waits for its payment and prints only
/// once paid; the QR, the countdown, the receipt with the fiscal QR and email,
/// the promo code, and what goes back after a cancellation.
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class PaymentFlowTests : IDisposable
{
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "neoprint-drive", Guid.NewGuid().ToString("N"));
    private readonly List<OrderTestKit> _kits = [];

    public PaymentFlowTests() => Directory.CreateDirectory(_drive);

    public void Dispose()
    {
        Directory.Delete(_drive, recursive: true);

        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private static string T(string key, params object?[] arguments) => Strings.Get("az", key, arguments);

    /// <summary>An order placed at a kiosk that takes payments, on the payment method screen.</summary>
    private async Task<(PrintFlowViewModel Flow, OrderTestKit Kit)> PlacedAsync(string? promo = null)
    {
        var kit = await OrderTestKit.CreateAsync(skipPayment: false);
        _kits.Add(kit);
        kit.Pdf(1, _drive, "report.pdf");

        var drives = new FakeRemovableDriveWatcher();
        drives.Drives.Add(new RemovableDrive(_drive, "USB", 1, 1));

        var flow = new PrintFlowViewModel(kit.Orders, drives, pageImage: _ => null)
        {
            Language = "az",
            Delay = (_, token) => Task.Delay(5, token),
        };

        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);

        if (promo is not null)
        {
            flow.EditPromoCommand.Execute(null);

            foreach (var key in promo)
            {
                flow.PromoKeyCommand.Execute(key.ToString());
            }

            await flow.ApplyPromoCommand.ExecuteAsync(null);
        }

        await flow.PlaceCommand.ExecuteAsync(null);

        return (flow, kit);
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            await Task.Delay(10);
        }

        done().Should().BeTrue($"waited for {what}");
    }

    [Fact]
    public async Task An_order_waits_for_its_payment_and_prints_only_once_the_bank_took_the_money()
    {
        var (flow, kit) = await PlacedAsync();

        flow.Stage.Should().Be(FlowStage.PaymentMethod);
        string.Join(' ', flow.Steps.Select(step => step.State.ToString()[0])).Should().Be("D D D C U");
        var job = kit.Journal.All().Should().ContainSingle().Subject;
        job.State.Should().Be(JournalState.AwaitingPayment, "nothing prints an unpaid order");

        await flow.PayWithBankCommand.ExecuteAsync(null);

        flow.Stage.Should().Be(FlowStage.QrPayment);
        flow.QrContent.Should().StartWith("MOCKBANK:");
        flow.IsAppPayment.Should().BeFalse();
        flow.PayInstruction.Should().Be(T("pay.scanBank"));
        flow.CountdownText.Should().MatchRegex(@"^Ödəniş üçün (5:00|4:5\d) vaxtınız var$");
        flow.CountdownLow.Should().BeFalse();

        // The bank took the money: the next look (or PaymentConfirmed) moves on.
        var payment = kit.Backend.Payments.Values.Single();
        kit.Backend.Fiscal[payment.Id] = new FiscalInfo(Guid.NewGuid(), "Issued", "N-1", "F-1", "https://e-kassa.mock/check/F-1", DateTime.UtcNow);
        kit.Backend.Payments[payment.Id] = payment with { Status = "Succeeded", PaidAt = DateTime.UtcNow };
        await flow.PaymentConfirmed(payment.Id);

        flow.Stage.Should().Be(FlowStage.Placed);
        flow.PlacedHeading.Should().Be(T("paid.title"));
        flow.HasPayment.Should().BeTrue();
        kit.Journal.Find(job.JobId)!.State.Should().Be(JournalState.Queued, "paid: it may print now");
        await UntilAsync(() => flow.HasFiscalQr, "the fiscal QR");
        flow.FiscalQr.Should().Be("https://e-kassa.mock/check/F-1");
        kit.Backend.Uploads.Should().BeEmpty("a bank payment has no account to keep the file in");
    }

    [Fact]
    public async Task Paid_with_the_app_the_file_goes_to_the_account_and_is_kept_to_retry_when_the_network_fails()
    {
        var (flow, kit) = await PlacedAsync();
        kit.Backend.UploadsDown = true;

        await flow.PayWithAppCommand.ExecuteAsync(null);
        flow.QrContent.Should().StartWith("neoprint://pay/");
        flow.PayInstruction.Should().Be(T("pay.scanApp"));

        var payment = kit.Backend.Payments.Values.Single();
        kit.Backend.Payments[payment.Id] = payment with { Status = "Succeeded", PaidAt = DateTime.UtcNow };
        await flow.RefreshPaymentAsync();

        flow.Stage.Should().Be(FlowStage.Placed);
        flow.PaidWithApp.Should().BeTrue();
        // Kept means on the waiting list, not just a file appearing in the spool:
        // the file is written before it is listed (T-118).
        await UntilAsync(() => kit.FlashUploads.Pending == 1, "the kept upload");

        // Back on the network: the next retry sends it, once.
        kit.Backend.UploadsDown = false;
        (await kit.Orders.SendPendingUploadsAsync(CancellationToken.None)).Should().Be(0);
        kit.Backend.Uploads.Should().ContainSingle().Which.FileName.Should().Be("report.pdf");
        (await kit.Orders.SendPendingUploadsAsync(CancellationToken.None)).Should().Be(0);
        kit.Backend.Uploads.Should().ContainSingle();
    }

    [Fact]
    public async Task When_the_time_runs_out_nothing_was_taken_and_the_customer_can_try_again()
    {
        var (flow, kit) = await PlacedAsync();
        await flow.PayWithBankCommand.ExecuteAsync(null);

        var payment = kit.Backend.Payments.Values.Single();
        kit.Backend.Payments[payment.Id] = payment with { Status = "Expired", QrContent = null };
        await flow.RefreshPaymentAsync();

        flow.Stage.Should().Be(FlowStage.PaymentExpired);
        flow.ExpiredTitle.Should().Be(T("pay.expiredTitle"));
        flow.ExpiredBody.Should().Be(T("pay.expiredBody"));

        flow.RetryPaymentCommand.Execute(null);
        flow.Stage.Should().Be(FlowStage.PaymentMethod);

        // Another way: the same order, a new intent.
        await flow.PayWithAppCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.QrPayment);
        kit.Backend.Payments.Values.Should().Contain(p => p.Method == "MobileApp" && p.IsOpen);
    }

    [Fact]
    public async Task A_promo_code_is_checked_before_the_order_and_goes_with_it()
    {
        var kitless = await PlacedAsync(promo: "NOPE-1");
        kitless.Flow.Stage.Should().Be(FlowStage.PaymentMethod, "a code that does not apply never blocks the order");
        kitless.Flow.PayPromoText.Should().BeNull();

        var kit = await OrderTestKit.CreateAsync(skipPayment: false);
        _kits.Add(kit);
        kit.Backend.Promos["YAY-10"] = 0.05m;
        kit.Pdf(1, _drive, "report2.pdf");
        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), pageImage: _ => null) { Language = "az", Delay = (_, t) => Task.Delay(5, t) };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single(entry => entry.Name == "report2.pdf"));
        flow.ContinueCommand.Execute(null);

        flow.EditPromoCommand.Execute(null);
        flow.IsTyping.Should().BeTrue("the keyboard opens on a sheet that hides the action row");

        foreach (var key in "NOPE")
        {
            flow.PromoKeyCommand.Execute(key.ToString());
        }

        await flow.ApplyPromoCommand.ExecuteAsync(null);
        flow.PromoProblem.Should().Be("Belə promokod yoxdur. Kodu yoxlayın.");
        flow.AppliedPromo.Should().BeNull();
        flow.CanPlace.Should().BeTrue("the customer may go on without it");
        flow.PromoEditing.Should().BeTrue("a refused code keeps the sheet open, with its reason");

        flow.CloseKeyboardCommand.Execute(null);
        flow.IsTyping.Should().BeFalse();
        flow.PromoProblem.Should().BeNull();
        flow.PromoInput.Should().Be("NOPE", "closing the sheet keeps what was typed");
        flow.EditPromoCommand.Execute(null);

        for (var i = 0; i < 4; i++)
        {
            flow.PromoEraseCommand.Execute(null);
        }

        foreach (var key in "YAY-10")
        {
            flow.PromoKeyCommand.Execute(key.ToString());
        }

        await flow.ApplyPromoCommand.ExecuteAsync(null);
        flow.AppliedPromo.Should().Be("YAY-10");
        flow.PromoLine.Should().Be(T("pay.promoLine", "YAY-10", Formats.Money(0.05m, "az")));
        flow.IsTyping.Should().BeFalse("an accepted code closes the sheet and brings back Sifariş et");

        await flow.PlaceCommand.ExecuteAsync(null);
        kit.Backend.Bodies[^1].Should().Contain("\"promoCode\":\"YAY-10\"");
        flow.PayPromoText.Should().Be(T("pay.promoLine", "YAY-10", Formats.Money(0.05m, "az")));
    }

    [Fact]
    public async Task The_receipt_is_emailed_to_a_checked_address_shown_back_masked()
    {
        var (flow, kit) = await PlacedAsync();
        await flow.PayWithBankCommand.ExecuteAsync(null);
        var payment = kit.Backend.Payments.Values.Single();
        kit.Backend.Payments[payment.Id] = payment with { Status = "Succeeded" };
        await flow.RefreshPaymentAsync();
        flow.IsTyping.Should().BeFalse("the receipt shows Bitir until the customer asks to type");

        flow.OpenEmailCommand.Execute(null);
        flow.IsTyping.Should().BeTrue();

        foreach (var key in "ad@")
        {
            flow.EmailKeyCommand.Execute(key.ToString());
        }

        await flow.SendEmailCommand.ExecuteAsync(null);
        flow.EmailProblem.Should().Be(T("paid.emailInvalid"), "said when Göndər is pressed, not on every key");
        kit.Backend.Emails.Should().BeEmpty();

        foreach (var key in "example.com")
        {
            flow.EmailKeyCommand.Execute(key.ToString());
        }

        await flow.SendEmailCommand.ExecuteAsync(null);
        flow.EmailProblem.Should().BeNull();
        kit.Backend.Emails.Should().ContainSingle().Which.Should().Be((payment.Id, "ad@example.com"));
        flow.EmailSent.Should().Be(T("paid.emailSent", "a***@example.com"));
        flow.EmailInput.Should().BeEmpty();
        flow.EmailEditing.Should().BeFalse("a sent receipt closes the sheet; the card shows where it went");
        flow.IsTyping.Should().BeFalse();
    }

    [Fact]
    public async Task A_refund_for_the_order_on_the_receipt_screen_is_shown_there_and_no_one_else_gets_it()
    {
        var (flow, kit) = await PlacedAsync();
        await flow.PayWithBankCommand.ExecuteAsync(null);
        var payment = kit.Backend.Payments.Values.Single();
        kit.Backend.Payments[payment.Id] = payment with { Status = "Succeeded" };
        await flow.RefreshPaymentAsync();
        flow.Stage.Should().Be(FlowStage.Placed);
        flow.PlacedHeading.Should().Be(T("paid.title"));

        // Another kiosk customer's order: not for this screen.
        flow.ShowPrinterRefund(Guid.NewGuid(), 0.30m, "OriginalMethod", "https://neoprint.test/refund/x").Should().BeFalse();
        flow.PrinterStopped.Should().BeFalse();

        // The printer stopped this order while the customer is still reading its receipt (T-133).
        flow.ShowPrinterRefund(payment.JobId, 0.30m, "OriginalMethod", "https://neoprint.test/refund/1").Should().BeTrue();
        flow.PrinterStopped.Should().BeTrue();
        flow.PlacedHeading.Should().Be(T("refund.printerTitle"), "it no longer says the order went well");
        flow.RefundText.Should().Be(T("refund.cardBody", Formats.Money(0.30m, "az")));
        flow.RefundQr.Should().Be("https://neoprint.test/refund/1");

        // Once they have gone, the next customer starts clean; a late refund goes to the home screen.
        flow.LeaveCommand.Execute(null);
        flow.PrinterStopped.Should().BeFalse();
        flow.RefundText.Should().BeNull();
        flow.ShowPrinterRefund(payment.JobId, 0.30m, "OriginalMethod", "https://neoprint.test/refund/1").Should().BeFalse();
    }

    [Fact]
    public async Task A_typing_sheet_never_stays_open_over_another_screen()
    {
        var (flow, _) = await PlacedAsync();
        flow.Stage.Should().Be(FlowStage.PaymentMethod);
        flow.IsTyping.Should().BeFalse("the promo sheet of the options screen closed with it");

        flow.EmailEditing = true;
        flow.LeaveCommand.Execute(null);

        flow.EmailEditing.Should().BeFalse();
        flow.IsTyping.Should().BeFalse("the next customer sees the actions of their screen");
    }

    [Fact]
    public async Task A_paid_order_cancelled_by_its_code_shows_what_goes_back_and_the_qr_to_follow_it()
    {
        var kit = await OrderTestKit.CreateAsync(skipPayment: false);
        _kits.Add(kit);
        kit.Backend.JobsByCode["482913"] = "Queued";

        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), pageImage: _ => null) { Language = "az", Delay = (_, t) => Task.Delay(5, t) };
        flow.StartCancel();

        foreach (var digit in "482913")
        {
            flow.DigitCommand.Execute(digit.ToString());
        }

        await flow.FindCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.CancelConfirm);

        // The backend refunds it a moment after the cancellation.
        var refundId = Guid.NewGuid();
        _ = Task.Delay(30).ContinueWith(_ =>
        {
            foreach (var jobId in kit.Backend.Cancelled)
            {
                kit.Backend.Refunds[jobId] = [new RefundInfo(refundId, 0.20m, "AZN", "Processing", "Card", $"http://site.test/refund/{refundId}", null)];
            }
        }, TaskScheduler.Default);

        await flow.ConfirmCancelCommand.ExecuteAsync(null);
        flow.Stage.Should().Be(FlowStage.CancelDone);

        await UntilAsync(() => flow.HasRefund, "the refund info");
        flow.RefundText.Should().Be(T("refund.cardBody", Formats.Money(0.20m, "az")));
        flow.RefundQr.Should().Be($"http://site.test/refund/{refundId}");
    }

    [Fact]
    public async Task Taking_the_drive_out_while_paying_does_not_lose_the_order()
    {
        var (flow, _) = await PlacedAsync();
        await flow.PayWithBankCommand.ExecuteAsync(null);

        flow.DriveRemoved();

        flow.Stage.Should().Be(FlowStage.QrPayment, "the file is in the spool already");
    }
}
