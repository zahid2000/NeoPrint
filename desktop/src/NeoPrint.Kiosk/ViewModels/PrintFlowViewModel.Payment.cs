using System.Globalization;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// Paying for the order (plan sections 7.3 screens 6–9, 9.1, 9.2;
/// docs/DESIGN_SYSTEM.md "Ödəniş ekranları"): the method, the QR with its
/// countdown, the receipt with the fiscal QR and email, and what goes back
/// after a cancellation. Also the promo code on the options screen.
/// </summary>
public sealed partial class PrintFlowViewModel
{
    /// <summary>How often the payment's status is asked for while its QR is shown (the fallback to <c>PaymentConfirmed</c>).</summary>
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);

    /// <summary>How long the receipt screen waits for the fiscal receipt.</summary>
    private static readonly TimeSpan FiscalWait = TimeSpan.FromSeconds(60);

    /// <summary>How long the cancel screen waits for the refund to be made.</summary>
    private static readonly TimeSpan RefundWait = TimeSpan.FromSeconds(20);

    private PrintJobInfo? _placedJob;
    private PaymentInfo? _payment;
    private CancellationTokenSource? _watch;
    private string? _emailKey;
    private int _promoCheck;

    /// <summary>How the flow waits between looks; a test makes it instant.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    // ---- promo code (options screen)

    [ObservableProperty]
    private bool _promoEditing;

    [ObservableProperty]
    private string _promoInput = string.Empty;

    [ObservableProperty]
    private string? _appliedPromo;

    [ObservableProperty]
    private string? _promoLine;

    [ObservableProperty]
    private string? _promoProblem;

    [ObservableProperty]
    private string? _amountDueText;

    // ---- payment

    [ObservableProperty]
    private string? _payAmountText;

    [ObservableProperty]
    private string? _payPromoText;

    [ObservableProperty]
    private string? _qrContent;

    [ObservableProperty]
    private bool _isAppPayment;

    [ObservableProperty]
    private string? _countdownText;

    [ObservableProperty]
    private bool _countdownLow;

    [ObservableProperty]
    private string? _payNotice;

    [ObservableProperty]
    private bool _paymentDeclined;

    // ---- receipt

    [ObservableProperty]
    private string? _fiscalQr;

    [ObservableProperty]
    private bool _fiscalPending;

    [ObservableProperty]
    private string _emailInput = string.Empty;

    [ObservableProperty]
    private string? _emailProblem;

    [ObservableProperty]
    private string? _emailSent;

    /// <summary>The email typing sheet is open (docs/DESIGN_SYSTEM.md, "Yazma paneli").</summary>
    [ObservableProperty]
    private bool _emailEditing;

    [ObservableProperty]
    private bool _paidWithApp;

    // ---- refund (after a cancellation, or the printer stopped this order)

    [ObservableProperty]
    private string? _refundText;

    [ObservableProperty]
    private string? _refundQr;

    /// <summary>
    /// The printer stopped the order on this receipt screen and the backend
    /// refunded it (scenario A): the screen says so where the customer is
    /// looking, not only on the home screen after "Bitir" (T-133).
    /// </summary>
    [ObservableProperty]
    private bool _printerStopped;

    // ---- texts

    public string PromoButtonText => T("promo.button");

    public string PromoApplyText => T("promo.apply");

    public string PromoRemoveText => T("promo.remove");

    public string PayTitle => T("pay.title");

    public string PayAmountLabel => T("pay.amount");

    public string PayBankText => T("pay.bank");

    public string PayBankHint => T("pay.bankHint");

    public string PayAppText => T("pay.app");

    public string PayAppHint => T("pay.appHint");

    public string PayInstruction => T(IsAppPayment ? "pay.scanApp" : "pay.scanBank");

    public string PayWaitingText => T("pay.waiting");

    public string OtherMethodText => T("pay.otherMethod");

    public string CancelOrderText => T("pay.cancelOrder");

    public string ExpiredTitle => T(PaymentDeclined ? "pay.declinedTitle" : "pay.expiredTitle");

    public string ExpiredBody => T(PaymentDeclined ? "pay.declinedBody" : "pay.expiredBody");

    public string RetryText => T("pay.retry");

    public string PaidTitle => T("paid.title");

    public string FiscalTitle => T("paid.fiscalTitle");

    public string FiscalScanText => T("paid.fiscalScan");

    public string FiscalPendingText => T("paid.fiscalPending");

    public string EmailTitle => T("paid.emailTitle");

    public string EmailSendText => T("paid.emailSend");

    public string EmailOpenText => T("paid.emailOpen");

    public string CloseKeyboardText => T("keyboard.close");

    /// <summary>
    /// A typing sheet (promo code or email) fills the screen: the action row
    /// under it is hidden, so the sheet's own button is the one primary.
    /// </summary>
    public bool IsTyping => PromoEditing || EmailEditing;

    public string AppNoteText => T("paid.appNote");

    public string RefundScanText => T("refund.scan");

    public bool HasFiscalQr => FiscalQr is not null;

    public bool HasRefund => RefundText is not null;

    /// <summary>The receipt screen is for a paid order; a Development order (no payment) has no receipt to send.</summary>
    public bool HasPayment => _payment is not null;

    /// <summary>The keys of the promo code keyboard: capitals, digits and the dash.</summary>
    public IReadOnlyList<IReadOnlyList<string>> PromoKeys { get; } =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P"],
        ["A", "S", "D", "F", "G", "H", "J", "K", "L", "-"],
        ["Z", "X", "C", "V", "B", "N", "M"],
    ];

    /// <summary>The keys of the email keyboard: what an address is made of.</summary>
    public IReadOnlyList<IReadOnlyList<string>> EmailKeys { get; } =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["q", "w", "e", "r", "t", "y", "u", "i", "o", "p"],
        ["a", "s", "d", "f", "g", "h", "j", "k", "l", "@"],
        ["z", "x", "c", "v", "b", "n", "m", ".", "-", "_"],
    ];

    // -----------------------------------------------------------------------
    // Promo code
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void EditPromo()
    {
        PromoEditing = !PromoEditing;
        PromoProblem = null;
    }

    [RelayCommand]
    private void PromoKey(string? key)
    {
        if (key is { Length: 1 } && PromoInput.Length < 32)
        {
            PromoInput += key;
            PromoProblem = null;
        }
    }

    [RelayCommand]
    private void PromoErase()
    {
        if (PromoInput.Length > 0)
        {
            PromoInput = PromoInput[..^1];
        }
    }

    [RelayCommand]
    private async Task ApplyPromoAsync()
    {
        if (PromoInput.Length == 0 || _quotedTotal is not { } total)
        {
            return;
        }

        await CheckPromoAsync(PromoInput, total, applying: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private void RemovePromo()
    {
        AppliedPromo = null;
        PromoInput = string.Empty;
        PromoLine = null;
        PromoProblem = null;
        AmountDueText = null;
        _promoCheck++;
    }

    /// <summary>
    /// What the code takes off this price. A code that stops applying (the
    /// price fell below its minimum) is shown with its reason and taken off:
    /// it never blocks the order.
    /// </summary>
    private async Task CheckPromoAsync(string code, decimal total, bool applying)
    {
        var check = ++_promoCheck;

        try
        {
            var result = await Task.Run(() => _orders.ValidatePromoAsync(code, total, CancellationToken.None)).ConfigureAwait(true);

            if (check != _promoCheck)
            {
                // The options changed meanwhile: a newer check answers.
                return;
            }

            if (result.Valid)
            {
                AppliedPromo = result.Code;
                PromoEditing = false;
                PromoProblem = null;
                PromoLine = T("pay.promoLine", result.Code, Formats.Money(result.Discount, Language));
                AmountDueText = T("promo.total", Formats.Money(result.AmountDue, Language));
                Track("promo_applied", ("code", result.Code));
            }
            else
            {
                PromoProblem = result.Reason ?? T("error.unexpected");
                PromoLine = null;
                AmountDueText = null;

                if (!applying)
                {
                    AppliedPromo = null;
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PromoProblem = T("error.network");
        }
        catch (KioskApiException exception)
        {
            PromoProblem = exception.Message;
        }
    }

    /// <summary>The price moved: a code already applied is worked out again on it.</summary>
    private void RequotePromo(decimal total)
    {
        if (AppliedPromo is { } code)
        {
            _ = CheckPromoAsync(code, total, applying: false);
        }
    }

    // -----------------------------------------------------------------------
    // Payment (screens 6–8)
    // -----------------------------------------------------------------------

    /// <summary>The order is made and waits to be paid: the method screen.</summary>
    private void AwaitPayment(PrintJobInfo job)
    {
        _placedJob = job;
        _payment = null;
        PlacedCode = job.JobCode;
        PayAmountText = Formats.Money(job.Price.AmountDue > 0 ? job.Price.AmountDue : job.Price.Total, Language);
        PayPromoText = job.Price.PromoCode is { } code && job.Price.PromoDiscount > 0
            ? T("pay.promoLine", code, Formats.Money(job.Price.PromoDiscount, Language))
            : null;
        PayNotice = null;
        PaymentDeclined = false;
        Stage = FlowStage.PaymentMethod;
    }

    [RelayCommand]
    private Task PayWithBankAsync() => OpenPaymentAsync("BankQr");

    [RelayCommand]
    private Task PayWithAppAsync() => OpenPaymentAsync("MobileApp");

    private async Task OpenPaymentAsync(string method)
    {
        if (_placedJob is not { } job)
        {
            return;
        }

        StopWatching();
        Stage = FlowStage.Working;
        Error = null;

        try
        {
            var payment = await Task.Run(() => _orders.OpenPaymentAsync(job.Id, method, CancellationToken.None)).ConfigureAwait(true);
            Track("payment_method", ("method", method));
            ShowPayment(payment);
        }
        catch (KioskApiException exception)
        {
            Error = exception.Message;
            Stage = FlowStage.PaymentMethod;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = T("error.network");
            Stage = FlowStage.PaymentMethod;
        }
    }

    private void ShowPayment(PaymentInfo payment)
    {
        _payment = payment;

        if (payment.IsPaid)
        {
            // Paid a moment ago (the same intent returned): the receipt.
            ShowPaid(payment);
            return;
        }

        IsAppPayment = payment.Method == "MobileApp";
        QrContent = payment.QrContent;
        PayAmountText = Formats.Money(payment.Amount, Language);
        PayPromoText = payment.PromoCode is { } code && payment.Discount > 0
            ? T("pay.promoLine", code, Formats.Money(payment.Discount, Language))
            : PayPromoText;
        PayNotice = payment.PromoRefusal is { } refusal ? T("pay.promoNotApplied", refusal) : null;
        UpdateCountdown();
        Stage = FlowStage.QrPayment;
        OnPropertyChanged(nameof(PayInstruction));

        _watch = new CancellationTokenSource();
        _ = WatchAsync(payment.Id, _watch.Token);
    }

    /// <summary>
    /// While the QR is shown: the countdown every second, the payment's
    /// status every few seconds - the fallback to <c>PaymentConfirmed</c>,
    /// which asks at once through <see cref="PaymentConfirmed"/>.
    /// </summary>
    private async Task WatchAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var sincePoll = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested && Stage == FlowStage.QrPayment && _payment?.Id == paymentId)
            {
                await Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
                UpdateCountdown();
                sincePoll += TimeSpan.FromSeconds(1);

                if (sincePoll >= PollEvery || _payment is { SecondsLeft: 0 } || SecondsLeft() == 0)
                {
                    sincePoll = TimeSpan.Zero;
                    await RefreshPaymentAsync().ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Left the screen.
        }
    }

    /// <summary>The hub said the payment went through: look now rather than at the next poll.</summary>
    public Task PaymentConfirmed(Guid paymentId)
        => _payment?.Id == paymentId && Stage == FlowStage.QrPayment ? RefreshPaymentAsync() : Task.CompletedTask;

    /// <summary>Asks the backend where the payment is and moves the screen with it.</summary>
    public async Task RefreshPaymentAsync()
    {
        if (_payment is not { } current || Stage != FlowStage.QrPayment)
        {
            return;
        }

        PaymentInfo latest;

        try
        {
            latest = await Task.Run(() => _orders.GetPaymentAsync(current.Id, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or KioskApiException)
        {
            // Not answered this time: the countdown goes on and the next poll asks again.
            return;
        }

        if (_payment?.Id != latest.Id || Stage != FlowStage.QrPayment)
        {
            return;
        }

        _payment = latest;

        if (latest.IsPaid)
        {
            ShowPaid(latest);
        }
        else if (latest.Status is "Expired" or "Failed" || latest.IsOpen && SecondsLeft() == 0 && latest.SecondsLeft == 0)
        {
            StopWatching();
            PaymentDeclined = latest.Status == "Failed";
            OnPropertyChanged(nameof(ExpiredTitle));
            OnPropertyChanged(nameof(ExpiredBody));
            Track("payment_ended", ("status", latest.Status));
            Stage = FlowStage.PaymentExpired;
        }
    }

    [RelayCommand]
    private void ChangeMethod()
    {
        StopWatching();
        PayNotice = null;
        Stage = FlowStage.PaymentMethod;
    }

    [RelayCommand]
    private void RetryPayment()
    {
        PaymentDeclined = false;
        Stage = FlowStage.PaymentMethod;
    }

    /// <summary>"Sifarişi ləğv et" before paying: the order goes, nothing is taken.</summary>
    [RelayCommand]
    private async Task CancelOrderAsync()
    {
        StopWatching();

        if (_placedJob is { } job)
        {
            try
            {
                await Task.Run(() => _orders.CancelAsync(job.Id, CancellationToken.None)).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or KioskApiException)
            {
                // The unpaid order expires with its intent anyway; nothing was taken.
            }
        }

        Leave();
    }

    private int SecondsLeft()
        => _payment is { } payment ? Math.Max(0, (int)Math.Ceiling((payment.ExpiresAt - DateTime.UtcNow).TotalSeconds)) : 0;

    private void UpdateCountdown()
    {
        var seconds = SecondsLeft();
        CountdownText = T("pay.timeLeft", $"{seconds / 60}:{seconds % 60:00}");
        CountdownLow = seconds <= 60;
    }

    private void StopWatching()
    {
        _watch?.Cancel();
        _watch?.Dispose();
        _watch = null;
    }

    // -----------------------------------------------------------------------
    // Receipt (screen 9)
    // -----------------------------------------------------------------------

    private void ShowPaid(PaymentInfo payment)
    {
        StopWatching();
        _payment = payment;
        _emailKey = null;
        EmailInput = string.Empty;
        EmailProblem = null;
        EmailSent = null;
        EmailEditing = false;
        FiscalQr = null;
        FiscalPending = true;
        PrinterStopped = false;
        RefundText = null;
        RefundQr = null;
        PaidWithApp = payment.Method is not "BankQr";

        if (_placedJob is { } job)
        {
            // It may print now, without waiting for the next look at the queue.
            _orders.MarkPaid(job.Id);
            PlacedCode = job.JobCode;

            if (PaidWithApp && _document?.SourcePath is { } original && File.Exists(original))
            {
                // Plan section 9.2, step 5: the file joins the account; a failure is kept and sent later.
                var name = FileName ?? Path.GetFileName(original);
                _ = Task.Run(() => _orders.KeepFlashFileAsync(job.Id, original, name, CancellationToken.None))
                    .ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        Track("payment_succeeded", ("method", payment.Method));
        _visitDone = true;
        Stage = FlowStage.Placed;
        OnPropertyChanged(nameof(HasPayment));
        OnPropertyChanged(nameof(PlacedHeading));

        _watch = new CancellationTokenSource();
        _ = WaitForFiscalAsync(payment.Id, _watch.Token);
    }

    private async Task WaitForFiscalAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;

        try
        {
            while (!cancellationToken.IsCancellationRequested && waited < FiscalWait && Stage == FlowStage.Placed)
            {
                FiscalInfo? fiscal = null;

                try
                {
                    fiscal = await Task.Run(() => _orders.GetFiscalAsync(paymentId, CancellationToken.None), cancellationToken).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is HttpRequestException or KioskApiException)
                {
                    // Asked again below.
                }

                if (fiscal is { IsIssued: true })
                {
                    FiscalQr = fiscal.VerificationUrl;
                    FiscalPending = false;
                    return;
                }

                await Delay(PollEvery, cancellationToken).ConfigureAwait(true);
                waited += PollEvery;
            }
        }
        catch (OperationCanceledException)
        {
            // Left the screen.
        }
    }

    [RelayCommand]
    private void OpenEmail()
    {
        EmailEditing = true;
        EmailProblem = null;
    }

    /// <summary>"Bağla" on a typing sheet: it closes, what was typed stays.</summary>
    [RelayCommand]
    private void CloseKeyboard()
    {
        PromoEditing = false;
        PromoProblem = null;
        EmailEditing = false;
        EmailProblem = null;
    }

    [RelayCommand]
    private void EmailKey(string? key)
    {
        if (key is { Length: 1 } && EmailInput.Length < 254)
        {
            EmailInput += key;
            EmailProblem = null;
        }
    }

    [RelayCommand]
    private void EmailErase()
    {
        if (EmailInput.Length > 0)
        {
            EmailInput = EmailInput[..^1];
        }
    }

    [RelayCommand]
    private async Task SendEmailAsync()
    {
        if (_payment is not { } payment)
        {
            return;
        }

        var address = EmailInput.Trim();

        if (!EmailPattern().IsMatch(address))
        {
            // Said when "Göndər" is pressed, not on every key (docs/DESIGN_SYSTEM.md, inputs).
            EmailProblem = T("paid.emailInvalid");
            return;
        }

        EmailProblem = null;
        var key = _emailKey ??= Guid.NewGuid().ToString("N");

        try
        {
            await Task.Run(() => _orders.EmailReceiptAsync(payment.Id, address, key, CancellationToken.None)).ConfigureAwait(true);
            _emailKey = null;
            EmailSent = T("paid.emailSent", Mask(address));
            EmailInput = string.Empty;
            EmailEditing = false;
            Track("receipt_emailed");
        }
        catch (KioskApiException exception)
        {
            _emailKey = null;
            EmailProblem = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The key is kept: sending again after a lost answer sends once.
            EmailProblem = T("error.network");
        }
    }

    /// <summary>The address as it is shown back on a public screen: <c>a***@example.com</c>.</summary>
    private static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? address : $"{address[0]}***{address[at..]}";
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    // -----------------------------------------------------------------------
    // What goes back after a cancellation (RefundInfo)
    // -----------------------------------------------------------------------

    /// <summary>A paid order was cancelled: the screen says what goes back, where, and how to follow it.</summary>
    private async Task ShowRefundAsync(Guid jobId)
    {
        _watch = new CancellationTokenSource();
        var token = _watch.Token;
        var waited = TimeSpan.Zero;

        try
        {
            while (!token.IsCancellationRequested && waited < RefundWait && Stage == FlowStage.CancelDone)
            {
                IReadOnlyList<RefundInfo> refunds = [];

                try
                {
                    refunds = await Task.Run(() => _orders.GetRefundsAsync(jobId, CancellationToken.None), token).ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is HttpRequestException or KioskApiException)
                {
                    // Asked again below.
                }

                if (refunds.Count > 0)
                {
                    RefundText = RefundBody(refunds[0].Destination, refunds.Sum(r => r.Amount));
                    RefundQr = refunds[0].StatusUrl;
                    return;
                }

                await Delay(PollEvery, token).ConfigureAwait(true);
                waited += PollEvery;
            }
        }
        catch (OperationCanceledException)
        {
            // Left the screen.
        }
    }

    /// <summary>
    /// The backend refunded an order the printer stopped. Shown here when the
    /// customer is still on this order's receipt screen; otherwise the caller
    /// shows it on the home screen.
    /// </summary>
    /// <returns>Whether this screen took it.</returns>
    public bool ShowPrinterRefund(Guid jobId, decimal amount, string destination, string? statusUrl)
    {
        if (Stage != FlowStage.Placed || _placedJob?.Id != jobId)
        {
            return false;
        }

        RefundText = RefundBody(destination, amount);
        RefundQr = statusUrl;
        PrinterStopped = true;
        return true;
    }

    private string RefundBody(string destination, decimal amount)
    {
        var text = Formats.Money(amount, Language);

        return destination switch
        {
            "Wallet" => T("refund.walletBody", text),
            "Voucher" => T("refund.voucherBody", text),
            _ => T("refund.cardBody", text),
        };
    }

    partial void OnPrinterStoppedChanged(bool value) => OnPropertyChanged(nameof(PlacedHeading));

    partial void OnPromoEditingChanged(bool value) => OnPropertyChanged(nameof(IsTyping));

    partial void OnEmailEditingChanged(bool value) => OnPropertyChanged(nameof(IsTyping));

    partial void OnFiscalQrChanged(string? value) => OnPropertyChanged(nameof(HasFiscalQr));

    partial void OnRefundTextChanged(string? value) => OnPropertyChanged(nameof(HasRefund));

    partial void OnIsAppPaymentChanged(bool value) => OnPropertyChanged(nameof(PayInstruction));

    /// <summary>Everything of the payment is dropped (Reset).</summary>
    private void ResetPayment()
    {
        StopWatching();
        _placedJob = null;
        _payment = null;
        _emailKey = null;
        PromoEditing = false;
        PromoInput = string.Empty;
        AppliedPromo = null;
        PromoLine = null;
        PromoProblem = null;
        AmountDueText = null;
        PayAmountText = null;
        PayPromoText = null;
        QrContent = null;
        CountdownText = null;
        PayNotice = null;
        PaymentDeclined = false;
        FiscalQr = null;
        FiscalPending = false;
        EmailInput = string.Empty;
        EmailProblem = null;
        EmailSent = null;
        EmailEditing = false;
        PaidWithApp = false;
        RefundText = null;
        RefundQr = null;
        PrinterStopped = false;
    }
}
