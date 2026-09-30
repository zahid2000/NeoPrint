using System.Net.Mail;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Fiscal;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Receipts;

namespace NeoPrint.Application.Receipts;

/// <summary>A payment went through: its receipt is numbered, once (plan section 2.12).</summary>
public sealed record CreateReceiptCommand(PaymentSucceeded Payment) : IRequest<Guid?>, ITransactionalRequest;

/// <summary>
/// Renders the receipt's PDF and stores it encrypted; when <paramref name="SendToAll"/>,
/// sends it again to everyone it went to (the fiscal receipt arrived).
/// </summary>
public sealed record RenderReceiptCommand(Guid ReceiptId, bool SendToAll = false) : IRequest<Unit>;

/// <summary>Step two of <see cref="RenderReceiptCommand"/>: the stored PDF recorded.</summary>
public sealed record RecordReceiptRenderCommand(Guid ReceiptId, FileObject Pdf, bool FiscalShown) : IRequest<Unit>, ITransactionalRequest;

/// <summary>The fiscal receipt of a payment was issued: its receipt is re-made with it.</summary>
public sealed record ReceiptFiscalIssuedCommand(Guid PaymentId) : IRequest<Unit>;

/// <summary>Receipts rendered before their fiscal receipt: re-made once it is there (the safety net for a missed event).</summary>
public sealed record CatchUpReceiptsCommand : IRequest<int>;

/// <summary>
/// "Qəbzi email-ə göndər" (plan section 6: <c>POST receipts/{id}/email</c>): from
/// the kiosk's receipt screen for the customer standing there, or from the app
/// for the account's own receipt.
/// </summary>
/// <param name="DeviceId">The kiosk asking; it may send only its own, fresh receipts.</param>
/// <param name="UserId">The account asking; only its own receipts, to its own address unless another is given.</param>
public sealed record EmailReceiptCommand(Guid ReceiptId, string? Email, Guid? DeviceId, Guid? UserId) : IRequest<ReceiptView>;

/// <summary>Step one of <see cref="EmailReceiptCommand"/>: checked and the address remembered.</summary>
public sealed record AddReceiptEmailCommand(Guid ReceiptId, string Email) : IRequest<Unit>, ITransactionalRequest;

/// <summary>An admin sends it again to the addresses it already went to.</summary>
public sealed record AdminResendReceiptCommand(Guid AdminUserId, Guid ReceiptId, string? Ip) : IRequest<ReceiptView>;

public sealed record ListMyReceiptsQuery(Guid UserId) : IRequest<IReadOnlyList<ReceiptView>>;

public sealed record ListReceiptsQuery(string? Number, Guid? PaymentId, int Page, int PageSize) : IRequest<ReceiptPage>;

/// <summary>The PDF, for its owner or an admin; re-made when the stored copy cannot be read.</summary>
/// <param name="UserId">Null for an admin.</param>
public sealed record GetReceiptPdfQuery(Guid ReceiptId, Guid? UserId) : IRequest<ReceiptPdf>;

public sealed record ReceiptPdf(string FileName, byte[] Content);

public sealed record ReceiptView(
    Guid Id,
    string Number,
    Guid PaymentId,
    Guid? JobId,
    Guid? KioskId,
    decimal Amount,
    string Currency,
    string Purpose,
    string FiscalStatus,
    bool FiscalShown,
    int EmailedCount,
    DateTime CreatedAt);

public sealed record ReceiptPage(IReadOnlyList<ReceiptView> Items, long Total, int Page, int PageSize);

public sealed class ReceiptHandlers :
    IRequestHandler<CreateReceiptCommand, Guid?>,
    IRequestHandler<RenderReceiptCommand, Unit>,
    IRequestHandler<RecordReceiptRenderCommand, Unit>,
    IRequestHandler<ReceiptFiscalIssuedCommand, Unit>,
    IRequestHandler<CatchUpReceiptsCommand, int>,
    IRequestHandler<EmailReceiptCommand, ReceiptView>,
    IRequestHandler<AddReceiptEmailCommand, Unit>,
    IRequestHandler<AdminResendReceiptCommand, ReceiptView>,
    IRequestHandler<ListMyReceiptsQuery, IReadOnlyList<ReceiptView>>,
    IRequestHandler<ListReceiptsQuery, ReceiptPage>,
    IRequestHandler<GetReceiptPdfQuery, ReceiptPdf>
{
    /// <summary>How long after paying the kiosk may still send the receipt: the customer is at the screen.</summary>
    private static readonly TimeSpan KioskEmailWindow = TimeSpan.FromMinutes(30);

    /// <summary>How far back the catch-up looks for receipts missing their fiscal part.</summary>
    private static readonly TimeSpan CatchUpWindow = TimeSpan.FromDays(7);

    private readonly ISender _sender;
    private readonly IReceiptRepository _receipts;
    private readonly IReceiptNumbers _numbers;
    private readonly IReceiptRenderer _renderer;
    private readonly IFileStorage _storage;
    private readonly IEmailSender _email;
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IUserRepository _users;
    private readonly IFiscalReceiptRepository _fiscal;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<ReceiptHandlers> _logger;

    public ReceiptHandlers(
        ISender sender,
        IReceiptRepository receipts,
        IReceiptNumbers numbers,
        IReceiptRenderer renderer,
        IFileStorage storage,
        IEmailSender email,
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IUserRepository users,
        IFiscalReceiptRepository fiscal,
        IAuditWriter audit,
        IClock clock,
        ILogger<ReceiptHandlers> logger)
    {
        _sender = sender;
        _receipts = receipts;
        _numbers = numbers;
        _renderer = renderer;
        _storage = storage;
        _email = email;
        _payments = payments;
        _jobs = jobs;
        _kiosks = kiosks;
        _devices = devices;
        _users = users;
        _fiscal = fiscal;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="CreateReceiptCommand" />
    public async Task<Guid?> Handle(CreateReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _receipts.FindByIdAsync(request.Payment.PaymentId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            // The same payment again: its receipt, not a second number.
            return existing.Id;
        }

        var payment = await _payments.FindByIdAsync(request.Payment.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");

        if (payment is not { WasPaid: true })
        {
            return null;
        }

        var now = _clock.UtcNow;
        var user = payment.UserId is { } userId ? await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) : null;
        var kiosk = payment.KioskId is { } kioskId ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) : null;
        var year = (payment.PaidAt ?? now).Year;
        var receipt = Receipt.Create(
            payment.Id,
            year,
            await _numbers.NextAsync(year, cancellationToken).ConfigureAwait(false),
            payment.UserId,
            payment.KioskId,
            payment.JobId,
            user?.Language ?? kiosk?.DefaultLanguage ?? "az",
            now);

        if (!await _receipts.TryAddAsync(receipt, cancellationToken).ConfigureAwait(false))
        {
            return payment.Id;
        }

        _logger.LogInformation("Receipt {Number} made for payment {PaymentId}.", receipt.Number, payment.Id);
        return receipt.Id;
    }

    /// <inheritdoc cref="RenderReceiptCommand" />
    public async Task<Unit> Handle(RenderReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("receipt");
        var (pdf, fiscalShown) = await RenderAndStoreAsync(receipt, cancellationToken).ConfigureAwait(false);

        if (request.SendToAll)
        {
            foreach (var address in receipt.EmailedTo)
            {
                await SendAsync(receipt, address, pdf, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Receipt {Number} rendered (fiscal shown: {FiscalShown}).", receipt.Number, fiscalShown);
        return Unit.Value;
    }

    /// <inheritdoc cref="RecordReceiptRenderCommand" />
    public async Task<Unit> Handle(RecordReceiptRenderCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("receipt");

        receipt.Rendered(request.Pdf, request.FiscalShown, _clock.UtcNow);
        await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }

    /// <inheritdoc cref="ReceiptFiscalIssuedCommand" />
    public async Task<Unit> Handle(ReceiptFiscalIssuedCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await _receipts.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false) is not { FiscalShown: false } receipt)
        {
            // Not made yet - its first rendering will carry the fiscal part - or already showing it.
            return Unit.Value;
        }

        return await _sender.Send(new RenderReceiptCommand(receipt.Id, SendToAll: true), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CatchUpReceiptsCommand" />
    public async Task<int> Handle(CatchUpReceiptsCommand request, CancellationToken cancellationToken)
    {
        var caughtUp = 0;

        foreach (var receipt in await _receipts.ListWithoutFiscalAsync(_clock.UtcNow - CatchUpWindow, 100, cancellationToken).ConfigureAwait(false))
        {
            var fiscal = await FiscalFor(receipt.PaymentId, cancellationToken).ConfigureAwait(false);

            if (fiscal is { Status: FiscalReceiptStatus.Issued } || receipt.Pdf is null)
            {
                await _sender.Send(new RenderReceiptCommand(receipt.Id, SendToAll: fiscal is { Status: FiscalReceiptStatus.Issued }), cancellationToken)
                    .ConfigureAwait(false);
                caughtUp++;
            }
        }

        return caughtUp;
    }

    /// <inheritdoc cref="EmailReceiptCommand" />
    public async Task<ReceiptView> Handle(EmailReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false);
        string? address = request.Email?.Trim();

        if (request.DeviceId is { } deviceId)
        {
            var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

            // Only the kiosk that took the payment, and only while its customer
            // can still be in front of it: a kiosk token is not a mailer.
            if (receipt is null || device?.KioskId is not { } kioskId || receipt.KioskId != kioskId || _clock.UtcNow - receipt.CreatedAt > KioskEmailWindow)
            {
                throw new NotFoundException("receipt");
            }
        }
        else if (request.UserId is { } userId)
        {
            if (receipt is null || receipt.UserId != userId)
            {
                throw new NotFoundException("receipt");
            }

            if (string.IsNullOrEmpty(address)
                && await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) is { EmailConfirmed: true } user)
            {
                address = user.Email;
            }
        }
        else
        {
            throw new NotFoundException("receipt");
        }

        if (string.IsNullOrEmpty(address) || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || address.Length > 254)
        {
            throw new ValidationException([new ValidationError("email", "receipts.emailInvalid")]);
        }

        await _sender.Send(new AddReceiptEmailCommand(receipt.Id, address), cancellationToken).ConfigureAwait(false);

        var pdf = await ReadOrRenderAsync(receipt, cancellationToken).ConfigureAwait(false);
        await SendAsync(receipt, address, pdf, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(receipt.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AddReceiptEmailCommand" />
    public async Task<Unit> Handle(AddReceiptEmailCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("receipt");

        if (receipt.AddEmail(request.Email))
        {
            await _receipts.UpdateAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }

    /// <inheritdoc cref="AdminResendReceiptCommand" />
    public async Task<ReceiptView> Handle(AdminResendReceiptCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("receipt");

        if (receipt.EmailedTo.Count == 0)
        {
            throw new DomainException("receipts.noEmail");
        }

        var pdf = await ReadOrRenderAsync(receipt, cancellationToken).ConfigureAwait(false);

        foreach (var address in receipt.EmailedTo)
        {
            await SendAsync(receipt, address, pdf, cancellationToken).ConfigureAwait(false);
        }

        // The addresses are personal data: the audit says how many, not which.
        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "receipts.resend",
                    nameof(Receipt),
                    receipt.Id.ToString(),
                    null,
                    JsonSerializer.Serialize(new { receipt.Number, recipients = receipt.EmailedTo.Count }),
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        return await ViewAsync(receipt.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ListMyReceiptsQuery" />
    public async Task<IReadOnlyList<ReceiptView>> Handle(ListMyReceiptsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var views = new List<ReceiptView>();

        foreach (var receipt in await _receipts.ListForUserAsync(request.UserId, 100, cancellationToken).ConfigureAwait(false))
        {
            views.Add(await ViewAsync(receipt, cancellationToken).ConfigureAwait(false));
        }

        return views;
    }

    /// <inheritdoc cref="ListReceiptsQuery" />
    public async Task<ReceiptPage> Handle(ListReceiptsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _receipts
            .SearchAsync(request.Number?.Trim().ToUpperInvariant(), request.PaymentId, (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);
        var views = new List<ReceiptView>(items.Count);

        foreach (var receipt in items)
        {
            views.Add(await ViewAsync(receipt, cancellationToken).ConfigureAwait(false));
        }

        return new ReceiptPage(views, total, page, size);
    }

    /// <inheritdoc cref="GetReceiptPdfQuery" />
    public async Task<ReceiptPdf> Handle(GetReceiptPdfQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receipt = await _receipts.FindByIdAsync(request.ReceiptId, cancellationToken).ConfigureAwait(false);

        if (receipt is null || request.UserId is { } userId && receipt.UserId != userId)
        {
            throw new NotFoundException("receipt");
        }

        return new ReceiptPdf($"{receipt.Number}.pdf", await ReadOrRenderAsync(receipt, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The stored PDF; rendered again when there is none or it cannot be read - it is only a rendering.</summary>
    private async Task<byte[]> ReadOrRenderAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.Pdf is { } stored)
        {
            try
            {
                using var buffer = new MemoryStream();
                await _storage.ReadAsync(ToStored(stored), StorageBucket.Receipts, buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Stored PDF of receipt {Number} could not be read; rendering it again.", receipt.Number);
            }
        }

        return (await RenderAndStoreAsync(receipt, cancellationToken).ConfigureAwait(false)).Pdf;
    }

    private async Task<(byte[] Pdf, bool FiscalShown)> RenderAndStoreAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        var content = await ContentAsync(receipt, cancellationToken).ConfigureAwait(false);
        var pdf = _renderer.Render(content);
        var fiscalShown = content.Fiscal is { Issued: true };

        using var stream = new MemoryStream(pdf, writable: false);
        var stored = await _storage
            .WriteAsync(StorageBucket.Receipts, $"receipts/{receipt.CreatedAt:yyyy}/{receipt.Id:N}.pdf", stream, "application/pdf", cancellationToken)
            .ConfigureAwait(false);

        await _sender.Send(
                new RecordReceiptRenderCommand(
                    receipt.Id,
                    new FileObject(stored.ProviderId, stored.Key, stored.Sha256, stored.EncryptedLength, stored.PlaintextLength, stored.KekId),
                    fiscalShown),
                cancellationToken)
            .ConfigureAwait(false);

        return (pdf, fiscalShown);
    }

    private async Task<ReceiptContent> ContentAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        var payment = await _payments.FindByIdAsync(receipt.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var job = receipt.JobId is { } jobId ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) : null;
        var kiosk = receipt.KioskId is { } kioskId ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) : null;
        var fiscal = await FiscalFor(payment.Id, cancellationToken).ConfigureAwait(false);
        var language = receipt.Language;
        var parts = payment.Parts ?? PaymentParts.ExternalOnly(payment.Amount);

        string Text(string key, params (string Name, object? Value)[] arguments) => Messages.Write(UserMessage.Of(key, arguments), language);

        var lines = new List<ReceiptLine>();

        if (payment.Purpose == PaymentPurpose.WalletTopUp)
        {
            lines.Add(new ReceiptLine(Text("receipt.line.topUp"), parts.Money.Amount));
        }
        else if (job is not null)
        {
            var price = job.Price.Price;
            lines.Add(new ReceiptLine(Text("receipt.line.print", ("sides", price.Sides), ("perSide", price.PricePerSide * price.PaperMultiplier)), price.BasePrice));

            var discount = price.BasePrice - price.FinalPrice;

            if (discount > 0)
            {
                lines.Add(new ReceiptLine(Text("receipt.line.discount"), -discount));
            }
            else if (discount < 0)
            {
                // The minimum price lifted the total.
                lines.Add(new ReceiptLine(Text("receipt.line.minimum"), -discount));
            }
        }

        var paidWith = new List<ReceiptLine>();

        if (parts.External.IsPositive)
        {
            paidWith.Add(new ReceiptLine(Text($"receipt.method.{(payment.Method == PaymentMethod.BankQr ? "bankQr" : "card")}"), parts.External.Amount));
        }

        if (parts.Wallet.IsPositive)
        {
            paidWith.Add(new ReceiptLine(Text("receipt.method.wallet"), parts.Wallet.Amount));
        }

        if (parts.BonusValue.IsPositive)
        {
            // The bonus part is 0 AZN on the fiscal receipt; shown here for what it covered.
            paidWith.Add(new ReceiptLine(Text("receipt.method.bonus", ("pages", parts.BonusPages)), parts.BonusValue.Amount));
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in new[]
                 {
                     "receipt.title", "receipt.number", "receipt.date", "receipt.kiosk", "receipt.job", "receipt.file", "receipt.total",
                     "receipt.paidWith", "receipt.fiscal.title", "receipt.fiscal.pending", "receipt.fiscal.number", "receipt.fiscal.id",
                     "receipt.fiscal.terminal", "receipt.fiscal.check", "receipt.notFiscal", "receipt.footer",
                 })
        {
            labels[key] = Text(key);
        }

        var summary = job is null
            ? null
            : Text(
                "receipt.printSummary",
                ("pages", job.Price.Price.Pages),
                ("copies", job.Options.Copies),
                ("paper", job.Options.Paper),
                ("color", Text(job.Options.Color ? "receipt.color" : "receipt.blackWhite")),
                ("sides", Text(job.Options.Duplex ? "receipt.duplex" : "receipt.simplex")));

        var fiscalPart = fiscal is null
            ? null
            : new ReceiptFiscal(fiscal.Status == FiscalReceiptStatus.Issued, fiscal.FiscalDocumentNumber, fiscal.FiscalId, fiscal.VerificationUrl, fiscal.TerminalId);

        return new ReceiptContent(
            language,
            receipt.Number,
            DateTime.SpecifyKind(payment.PaidAt ?? receipt.CreatedAt, DateTimeKind.Utc),
            kiosk?.Code,
            kiosk?.Location.Address,
            job?.JobCode,
            job?.LocalFileName,
            summary,
            lines,
            parts.Money.Amount,
            payment.Amount.Currency,
            paidWith,
            parts.BonusPages,
            fiscalPart,
            fiscalPart is { Issued: true, VerificationUrl: { } url } ? url : null,
            labels);
    }

    private async Task<FiscalReceipt?> FiscalFor(Guid paymentId, CancellationToken cancellationToken)
        => await _fiscal.FindByDedupKeyAsync(FiscalizationHandlers.SaleKey(paymentId), cancellationToken).ConfigureAwait(false)
           ?? await _fiscal.FindByDedupKeyAsync(FiscalizationHandlers.TopUpKey(paymentId), cancellationToken).ConfigureAwait(false);

    private async Task SendAsync(Receipt receipt, string address, byte[] pdf, CancellationToken cancellationToken)
    {
        var language = receipt.Language;

        await _email.SendAsync(
                new EmailMessage(
                    address,
                    Messages.Write(UserMessage.Of("receipt.email.subject", ("number", receipt.Number)), language),
                    Messages.Write(UserMessage.Of("receipt.email.body", ("number", receipt.Number)), language),
                    [new EmailAttachment($"{receipt.Number}.pdf", "application/pdf", pdf)]),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReceiptView> ViewAsync(Guid receiptId, CancellationToken cancellationToken)
        => await ViewAsync(
                await _receipts.FindByIdAsync(receiptId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("receipt"),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ReceiptView> ViewAsync(Receipt receipt, CancellationToken cancellationToken)
    {
        var payment = await _payments.FindByIdAsync(receipt.PaymentId, cancellationToken).ConfigureAwait(false);
        var fiscal = await FiscalFor(receipt.PaymentId, cancellationToken).ConfigureAwait(false);

        return new ReceiptView(
            receipt.Id,
            receipt.Number,
            receipt.PaymentId,
            receipt.JobId,
            receipt.KioskId,
            (payment?.Parts?.Money ?? payment?.Amount ?? Money.Zero).Amount,
            payment?.Amount.Currency ?? "AZN",
            payment?.Purpose.ToString() ?? string.Empty,
            fiscal?.Status.ToString() ?? "None",
            receipt.FiscalShown,
            receipt.EmailedTo.Count,
            receipt.CreatedAt);
    }

    private static StoredObject ToStored(FileObject pdf)
        => new(pdf.ProviderId, pdf.Key, pdf.Sha256, pdf.EncryptedLength, pdf.PlaintextLength, pdf.KekId);
}

