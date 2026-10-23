using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Fiscal;

/// <summary>A payment went through: its sale (or top-up) receipt is made, once (plan section 2.12).</summary>
public sealed record FiscalizePaymentCommand(PaymentSucceeded Payment) : IRequest<Guid?>, ITransactionalRequest;

/// <summary>Money went back: its refund receipt is made, once.</summary>
public sealed record FiscalizeRefundCommand(RefundCompleted Refund) : IRequest<Guid?>, ITransactionalRequest;

/// <summary>Asks the provider for a receipt that is due; the answer is recorded, a failure waits for its next try.</summary>
public sealed record IssueFiscalReceiptCommand(Guid ReceiptId) : IRequest<string>;

/// <summary>Step one of <see cref="IssueFiscalReceiptCommand"/>: checked and counted; the request to send, or why not now.</summary>
public sealed record StartFiscalAttemptCommand(Guid ReceiptId) : IRequest<FiscalAttempt>, ITransactionalRequest;

/// <summary>Step two: what the provider said.</summary>
public sealed record RecordFiscalOutcomeCommand(Guid ReceiptId, FiscalProviderResult Result) : IRequest<string>, ITransactionalRequest;

/// <summary><c>Fiscal.MaxPendingMinutes</c> passed without a receipt: an incident (High).</summary>
public sealed record ReportLateFiscalReceiptsCommand : IRequest<int>;

/// <summary>"Təkrarla" in the panel (plan section 8A.3, "Fiskal çeklər").</summary>
public sealed record AdminRetryFiscalReceiptCommand(Guid AdminUserId, Guid ReceiptId, string? Ip) : IRequest<FiscalReceiptView>, ITransactionalRequest;

public sealed record GetFiscalReceiptQuery(Guid ReceiptId) : IRequest<FiscalReceiptView>;

public sealed record ListFiscalReceiptsQuery(
    Guid AdminUserId, IReadOnlyCollection<string>? Statuses, IReadOnlyCollection<string>? Types, Guid? PaymentId, int Page, int PageSize)
    : IRequest<FiscalReceiptPage>;

/// <summary>The receipt's fiscal part, for the kiosk's receipt screen, the app and the PDF.</summary>
public sealed record GetPaymentFiscalQuery(Guid PaymentId) : IRequest<FiscalSummary?>;

/// <param name="Request">Null when nothing is to be sent now; <see cref="Reason"/> says why.</param>
public sealed record FiscalAttempt(FiscalRequest? Request, string? Reason);

public sealed record FiscalReceiptView(
    Guid Id,
    string Type,
    string Status,
    Guid? PaymentId,
    Guid? RefundId,
    Guid? KioskId,
    string? TerminalId,
    decimal Amount,
    string Currency,
    IReadOnlyList<FiscalLineView> Lines,
    IReadOnlyList<FiscalPaidView> PaidWith,
    string? FiscalDocumentNumber,
    string? FiscalId,
    string? VerificationUrl,
    string? OriginalFiscalId,
    int Attempts,
    DateTime? NextAttemptAt,
    string? LastError,
    DateTime CreatedAt,
    DateTime? IssuedAt)
{
    public static FiscalReceiptView From(FiscalReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new FiscalReceiptView(
            receipt.Id,
            receipt.Type.ToString(),
            receipt.Status.ToString(),
            receipt.PaymentId,
            receipt.RefundId,
            receipt.KioskId,
            receipt.TerminalId,
            receipt.Amount.Amount,
            receipt.Amount.Currency,
            [.. receipt.Lines.Select(line => new FiscalLineView(line.Name, line.Quantity, line.UnitPrice.Amount, line.Total.Amount))],
            [.. receipt.PaidWith.Select(part => new FiscalPaidView(part.Kind, part.Amount.Amount))],
            receipt.FiscalDocumentNumber,
            receipt.FiscalId,
            receipt.VerificationUrl,
            receipt.OriginalFiscalId,
            receipt.Attempts,
            receipt.NextAttemptAt,
            receipt.LastError is { } error && Messages.Has(error) ? Messages.Write(new UserMessage(error)) : receipt.LastError,
            receipt.CreatedAt,
            receipt.IssuedAt);
    }
}

public sealed record FiscalLineView(string Name, int Quantity, decimal UnitPrice, decimal Total);

public sealed record FiscalPaidView(string Kind, decimal Amount);

public sealed record FiscalReceiptPage(IReadOnlyList<FiscalReceiptView> Items, long Total, int Page, int PageSize);

/// <param name="Status">Pending while the e-kassa has not answered: the receipt says "Fiskal çek hazırlanır".</param>
public sealed record FiscalSummary(Guid ReceiptId, string Status, string? FiscalDocumentNumber, string? FiscalId, string? VerificationUrl, DateTime? IssuedAt);

public sealed class FiscalizationHandlers :
    IRequestHandler<FiscalizePaymentCommand, Guid?>,
    IRequestHandler<FiscalizeRefundCommand, Guid?>,
    IRequestHandler<IssueFiscalReceiptCommand, string>,
    IRequestHandler<StartFiscalAttemptCommand, FiscalAttempt>,
    IRequestHandler<RecordFiscalOutcomeCommand, string>,
    IRequestHandler<ReportLateFiscalReceiptsCommand, int>,
    IRequestHandler<AdminRetryFiscalReceiptCommand, FiscalReceiptView>,
    IRequestHandler<GetFiscalReceiptQuery, FiscalReceiptView>,
    IRequestHandler<ListFiscalReceiptsQuery, FiscalReceiptPage>,
    IRequestHandler<GetPaymentFiscalQuery, FiscalSummary?>
{
    /// <summary>The longest wait between tries of a provider that is down.</summary>
    private const int MaxBackoffMinutes = 30;

    /// <summary>Line texts are the tax authority's: always in Azerbaijani.</summary>
    private const string ReceiptLanguage = "az";

    private readonly ISender _sender;
    private readonly IFiscalReceiptRepository _receipts;
    private readonly IFiscalProvider _provider;
    private readonly IPaymentRepository _payments;
    private readonly IRefundRepository _refunds;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly IIncidentRepository _incidents;
    private readonly IncidentReporter _reporter;
    private readonly IPaymentsAdminNotifier _notifier;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly Admin.IPermissionResolver _permissions;
    private readonly ILogger<FiscalizationHandlers> _logger;

    public FiscalizationHandlers(
        ISender sender,
        IFiscalReceiptRepository receipts,
        IFiscalProvider provider,
        IPaymentRepository payments,
        IRefundRepository refunds,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        IIncidentRepository incidents,
        IncidentReporter reporter,
        IPaymentsAdminNotifier notifier,
        ISettingsReader settings,
        IAuditWriter audit,
        IEventPublisher events,
        ICorrelationContext correlation,
        Admin.IPermissionResolver permissions,
        IClock clock,
        ILogger<FiscalizationHandlers> logger)
    {
        _sender = sender;
        _receipts = receipts;
        _provider = provider;
        _payments = payments;
        _refunds = refunds;
        _jobs = jobs;
        _kiosks = kiosks;
        _incidents = incidents;
        _reporter = reporter;
        _notifier = notifier;
        _settings = settings;
        _audit = audit;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _permissions = permissions;
        _logger = logger;
    }

    public static string SaleKey(Guid paymentId) => $"sale:{paymentId}";

    public static string TopUpKey(Guid paymentId) => $"topup:{paymentId}";

    public static string RefundKey(Guid refundId) => $"refund:{refundId}";

    /// <inheritdoc cref="FiscalizePaymentCommand" />
    public async Task<Guid?> Handle(FiscalizePaymentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.Payment.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");

        if (payment is not { WasPaid: true, Parts: { } parts })
        {
            return null;
        }

        var now = _clock.UtcNow;
        var mode = await _settings.GetAsync(SettingKeys.FiscalTopUpReceiptMode, cancellationToken).ConfigureAwait(false) ?? "OnTopUp";
        var advance = string.Equals(mode, "OnTopUp", StringComparison.OrdinalIgnoreCase);
        var terminal = await TerminalAsync(payment, cancellationToken).ConfigureAwait(false);
        FiscalReceipt receipt;

        if (payment.Purpose == PaymentPurpose.WalletTopUp)
        {
            if (!advance)
            {
                // OnService: the wallet's money is fiscalized when it pays for a print.
                return null;
            }

            receipt = FiscalReceipt.Create(
                FiscalReceiptType.TopUp,
                TopUpKey(payment.Id),
                parts.Money,
                [Line("fiscal.line.topUp", parts.Money)],
                [new FiscalPaymentPart(payment.Method.ToString(), parts.Money)],
                now,
                paymentId: payment.Id,
                terminalId: terminal);
        }
        else
        {
            // The bonus part is 0 AZN and not on the receipt (plan section 2.12).
            if (!parts.Money.IsPositive)
            {
                return null;
            }

            var job = payment.JobId is { } jobId ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) : null;
            var paidWith = new List<FiscalPaymentPart>();

            if (parts.External.IsPositive)
            {
                paidWith.Add(new FiscalPaymentPart(payment.Method == PaymentMethod.BankQr ? "BankQr" : "Card", parts.External));
            }

            if (parts.Wallet.IsPositive)
            {
                // Paid in advance when top-ups are fiscalized; otherwise this sale is the wallet money's receipt.
                paidWith.Add(new FiscalPaymentPart(advance ? "Advance" : "Wallet", parts.Wallet));
            }

            receipt = FiscalReceipt.Create(
                FiscalReceiptType.Sale,
                SaleKey(payment.Id),
                parts.Money,
                [Line("fiscal.line.print", parts.Money, ("jobCode", job?.JobCode ?? "-"))],
                paidWith,
                now,
                paymentId: payment.Id,
                kioskId: payment.KioskId,
                terminalId: terminal);
        }

        return await AddAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="FiscalizeRefundCommand" />
    public async Task<Guid?> Handle(FiscalizeRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.Refund.RefundId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("refund");

        // Bonus pages are not money and never were on the sale's receipt.
        if (refund.Status != RefundStatus.Succeeded || !refund.Amount.IsPositive)
        {
            return null;
        }

        var payment = await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var sale = await _receipts.FindByDedupKeyAsync(SaleKey(payment.Id), cancellationToken).ConfigureAwait(false);
        var mode = await _settings.GetAsync(SettingKeys.FiscalTopUpReceiptMode, cancellationToken).ConfigureAwait(false) ?? "OnTopUp";
        var kind = refund.Source == RefundSource.Wallet
            ? string.Equals(mode, "OnTopUp", StringComparison.OrdinalIgnoreCase) ? "Advance" : "Wallet"
            : payment.Method == PaymentMethod.BankQr ? "BankQr" : "Card";

        var receipt = FiscalReceipt.Create(
            FiscalReceiptType.Refund,
            RefundKey(refund.Id),
            refund.Amount,
            [Line("fiscal.line.refund", refund.Amount)],
            [new FiscalPaymentPart(kind, refund.Amount)],
            _clock.UtcNow,
            paymentId: payment.Id,
            refundId: refund.Id,
            kioskId: payment.KioskId,
            // The refund goes through the till that made the sale.
            terminalId: sale?.TerminalId ?? await TerminalAsync(payment, cancellationToken).ConfigureAwait(false));

        return await AddAsync(receipt, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="IssueFiscalReceiptCommand" />
    public async Task<string> Handle(IssueFiscalReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempt = await _sender.Send(new StartFiscalAttemptCommand(request.ReceiptId), cancellationToken).ConfigureAwait(false);

        if (attempt.Request is not { } fiscal)
        {
            return attempt.Reason ?? "NotDue";
        }

        FiscalProviderResult result;

        try
        {
            // Outside any transaction: the e-kassa is another system; the key is
            // the receipt's id, so asking again returns the same document.
            result = fiscal.Type == FiscalReceiptType.Refund
                ? await _provider.IssueRefundAsync(fiscal, cancellationToken).ConfigureAwait(false)
                : await _provider.IssueSaleAsync(fiscal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException
                                              || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Fiscal provider {Provider} did not answer for receipt {ReceiptId}: {Error}.", _provider.Name, request.ReceiptId, exception.GetType().Name);
            result = new FiscalProviderResult(FiscalOutcome.Unavailable, FailureCode: "unavailable");
        }

        return await _sender.Send(new RecordFiscalOutcomeCommand(request.ReceiptId, result), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="StartFiscalAttemptCommand" />
    public async Task<FiscalAttempt> Handle(StartFiscalAttemptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("fiscalReceipt");
        var now = _clock.UtcNow;

        if (receipt.Status != FiscalReceiptStatus.Pending)
        {
            return new FiscalAttempt(null, receipt.Status.ToString());
        }

        if (receipt.NextAttemptAt is { } due && due > now)
        {
            return new FiscalAttempt(null, "NotDue");
        }

        if (!await _settings.GetBoolAsync(SettingKeys.FiscalEnabled, true, cancellationToken).ConfigureAwait(false))
        {
            // Switched off by an admin: the receipt waits, nothing is lost.
            receipt.Fail("fiscal.disabled", now.AddMinutes(1), System, now);
            await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
            return new FiscalAttempt(null, "Disabled");
        }

        if (receipt.TerminalId is null)
        {
            // A setup gap, not the provider's fault: the incident after
            // Fiscal.MaxPendingMinutes tells the admin; the receipt waits.
            receipt.CountAttempt(now);
            receipt.Fail("fiscal.noTerminal", now.AddMinutes(Backoff(receipt.Attempts)), System, now);
            await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
            return new FiscalAttempt(null, "NoTerminal");
        }

        string? original = null;

        if (receipt.Type == FiscalReceiptType.Refund)
        {
            var sale = receipt.PaymentId is { } paymentId
                ? await _receipts.FindByDedupKeyAsync(SaleKey(paymentId), cancellationToken).ConfigureAwait(false)
                : null;

            if (sale is not { Status: FiscalReceiptStatus.Issued, FiscalId: { } saleFiscalId })
            {
                // A refund receipt names the sale's fiscal document: it waits for it.
                receipt.CountAttempt(now);
                receipt.Fail("fiscal.waitingForSale", now.AddMinutes(1), System, now);
                await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
                return new FiscalAttempt(null, "WaitingForSale");
            }

            original = saleFiscalId;
            receipt.OriginalFiscalId = saleFiscalId;
        }

        receipt.CountAttempt(now);
        await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);

        return new FiscalAttempt(
            new FiscalRequest(
                receipt.Id.ToString("N"),
                receipt.Type,
                receipt.TerminalId,
                receipt.Amount.Amount,
                receipt.Amount.Currency,
                receipt.Lines,
                receipt.PaidWith,
                original),
            null);
    }

    /// <inheritdoc cref="RecordFiscalOutcomeCommand" />
    public async Task<string> Handle(RecordFiscalOutcomeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("fiscalReceipt");

        if (receipt.Status != FiscalReceiptStatus.Pending)
        {
            return receipt.Status.ToString();
        }

        var now = _clock.UtcNow;
        var result = request.Result;

        switch (result.Outcome)
        {
            case FiscalOutcome.Issued when result is { DocumentNumber: { } number, FiscalId: { } fiscalId }:
                receipt.Issue(number, fiscalId, result.VerificationUrl, System, now);
                await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
                await CloseIncidentsAsync(receipt, cancellationToken).ConfigureAwait(false);
                await _events.PublishAsync(
                        new FiscalReceiptIssued(receipt.Id, receipt.Type.ToString(), receipt.PaymentId, receipt.RefundId, _correlation.CorrelationId),
                        cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Fiscal receipt {ReceiptId} ({Type}) issued as {Number}.", receipt.Id, receipt.Type, number);
                break;

            case FiscalOutcome.Rejected:
                receipt.Fail(ProviderError(result.FailureCode, "fiscal.provider.rejected"), null, System, now);
                await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
                await _reporter.RaiseAsync(
                        IncidentType.FiscalFailed,
                        IncidentSeverity.High,
                        UserMessage.Of("incidents.fiscalRejected", ("type", receipt.Type.ToString()), ("amount", receipt.Amount.Amount)),
                        $"fiscal-failed:{receipt.Id}",
                        cancellationToken,
                        receipt.KioskId,
                        nameof(FiscalReceipt),
                        receipt.Id)
                    .ConfigureAwait(false);
                _logger.LogWarning("Fiscal receipt {ReceiptId} refused by the provider ({Code}).", receipt.Id, result.FailureCode);
                break;

            default:
                receipt.Fail(ProviderError(result.FailureCode, "fiscal.provider.unavailable"), now.AddMinutes(Backoff(receipt.Attempts)), System, now);
                await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Fiscal receipt {ReceiptId} try {Attempt} failed ({Code}); next try at {Next:O}.", receipt.Id, receipt.Attempts, result.FailureCode, receipt.NextAttemptAt);
                break;
        }

        await _notifier.FiscalStatusChangedAsync(receipt.Id, receipt.Status.ToString(), cancellationToken, receipt.KioskId).ConfigureAwait(false);

        return receipt.Status.ToString();
    }

    /// <inheritdoc cref="ReportLateFiscalReceiptsCommand" />
    public async Task<int> Handle(ReportLateFiscalReceiptsCommand request, CancellationToken cancellationToken)
    {
        if (!await _settings.GetBoolAsync(SettingKeys.FiscalEnabled, true, cancellationToken).ConfigureAwait(false))
        {
            // Switched off on purpose: waiting receipts are the admin's own decision.
            return 0;
        }

        var minutes = Math.Max(1, await _settings.GetIntAsync(SettingKeys.FiscalMaxPendingMinutes, 15, cancellationToken).ConfigureAwait(false));
        var late = await _receipts.ListPendingSinceAsync(_clock.UtcNow.AddMinutes(-minutes), 100, cancellationToken).ConfigureAwait(false);

        foreach (var receipt in late)
        {
            await _reporter.RaiseAsync(
                    IncidentType.FiscalFailed,
                    IncidentSeverity.High,
                    UserMessage.Of(
                        "incidents.fiscalPending",
                        ("type", receipt.Type.ToString()),
                        ("amount", receipt.Amount.Amount),
                        ("minutes", minutes),
                        // The cause's key, not its text: read in the reader's language (T-140).
                        ("causeMessage", receipt.LastError is { } error && Messages.Has(error) ? error : "incidents.fiscalPendingNoCause")),
                    $"fiscal-pending:{receipt.Id}",
                    cancellationToken,
                    receipt.KioskId,
                    nameof(FiscalReceipt),
                    receipt.Id)
                .ConfigureAwait(false);
        }

        return late.Count;
    }

    /// <inheritdoc cref="AdminRetryFiscalReceiptCommand" />
    public async Task<FiscalReceiptView> Handle(AdminRetryFiscalReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("fiscalReceipt");
        var before = receipt.Status.ToString();

        receipt.Retry(ActorContext.Admin(request.AdminUserId, "fiscal.reason.retried", _correlation.CorrelationId), _clock.UtcNow);
        await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "fiscal.receipts.retry",
                    nameof(FiscalReceipt),
                    receipt.Id.ToString(),
                    before,
                    JsonSerializer.Serialize(new { status = receipt.Status.ToString() }),
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        return FiscalReceiptView.From(receipt);
    }

    /// <inheritdoc cref="GetFiscalReceiptQuery" />
    public async Task<FiscalReceiptView> Handle(GetFiscalReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return FiscalReceiptView.From(
            await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("fiscalReceipt"));
    }

    /// <inheritdoc cref="ListFiscalReceiptsQuery" />
    public async Task<FiscalReceiptPage> Handle(ListFiscalReceiptsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statuses = Parse<FiscalReceiptStatus>(request.Statuses, "status");
        var types = Parse<FiscalReceiptType>(request.Types, "type");
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        // Only the kiosks of the account's scope (T-301); null when it sees them all.
        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        IReadOnlyCollection<Guid>? kiosks = effective.AllKiosks ? null : effective.KioskIds;
        var (items, total) = await _receipts.SearchAsync(statuses, types, request.PaymentId, (page - 1) * size, size, cancellationToken, kiosks).ConfigureAwait(false);

        return new FiscalReceiptPage([.. items.Select(FiscalReceiptView.From)], total, page, size);
    }

    /// <inheritdoc cref="GetPaymentFiscalQuery" />
    public async Task<FiscalSummary?> Handle(GetPaymentFiscalQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByDedupKeyAsync(SaleKey(request.PaymentId), cancellationToken).ConfigureAwait(false)
                      ?? await _receipts.FindByDedupKeyAsync(TopUpKey(request.PaymentId), cancellationToken).ConfigureAwait(false);

        return receipt is null
            ? null
            : new FiscalSummary(receipt.Id, receipt.Status.ToString(), receipt.FiscalDocumentNumber, receipt.FiscalId, receipt.VerificationUrl, receipt.IssuedAt);
    }

    private static ActorContext System => ActorContext.System("fiscal.reason.provider");

    /// <summary>The provider's code as a message key when the catalogs know it; its own words never reach a person.</summary>
    private static string ProviderError(string? code, string fallback)
        => code is { Length: > 0 } && Messages.Has("fiscal.provider." + code) ? "fiscal.provider." + code : fallback;

    private static int Backoff(int attempts) => (int)Math.Min(MaxBackoffMinutes, Math.Pow(2, Math.Max(0, attempts - 1)));

    private static FiscalLine Line(string key, Money amount, params (string Name, object? Value)[] arguments)
        => new(Messages.Write(UserMessage.Of(key, arguments), ReceiptLanguage), 1, amount, amount);

    private static List<T>? Parse<T>(IReadOnlyCollection<string>? values, string field)
        where T : struct, Enum
        => values?
            .Select(value => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException([new ValidationError(field, "fiscal.filterInvalid")]))
            .ToList();

    /// <summary>A kiosk sale through the kiosk's till; an app sale or top-up through the online one (plan section 2.12).</summary>
    private async Task<string?> TerminalAsync(Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Channel == PaymentChannel.Kiosk && payment.KioskId is { } kioskId
            && await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) is { FiscalTerminalId: { Length: > 0 } kioskTerminal })
        {
            return kioskTerminal;
        }

        if (payment.Channel == PaymentChannel.Kiosk)
        {
            // A kiosk without its own till: a setup gap the incident will show, never another till's sale.
            return null;
        }

        var online = await _settings.GetAsync(SettingKeys.FiscalOnlineTerminalId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(online) ? null : online.Trim();
    }

    private async Task<Guid?> AddAsync(FiscalReceipt receipt, CancellationToken cancellationToken)
    {
        if (!await _receipts.TryAddAsync(receipt, cancellationToken).ConfigureAwait(false))
        {
            // The same sale or refund again: one receipt, not two.
            return (await _receipts.FindByDedupKeyAsync(receipt.DedupKey, cancellationToken).ConfigureAwait(false))?.Id;
        }

        _logger.LogInformation("Fiscal receipt {ReceiptId} ({Type}, {Amount}) created for {DedupKey}.", receipt.Id, receipt.Type, receipt.Amount.Amount, receipt.DedupKey);
        return receipt.Id;
    }

    private async Task CloseIncidentsAsync(FiscalReceipt receipt, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { $"fiscal-pending:{receipt.Id}", $"fiscal-failed:{receipt.Id}" })
        {
            if (await _incidents.FindOpenByDedupKeyAsync(key, cancellationToken).ConfigureAwait(false) is { } incident)
            {
                incident.Resolve(null, UserMessage.Of("incidents.resolution.fiscalIssued", ("number", receipt.FiscalDocumentNumber)).Encode(), _clock.UtcNow);
                await _incidents.UpdateAsync(incident, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
