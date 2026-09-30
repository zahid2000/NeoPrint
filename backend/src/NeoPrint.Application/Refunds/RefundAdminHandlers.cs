using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Wallets;
using NeoPrint.Contracts;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Refunds;

/// <summary>What an admin may refund on a payment (plan section 6: <c>GET admin/payments/{id}/refund-options</c>).</summary>
public sealed record GetRefundOptionsQuery(Guid PaymentId) : IRequest<RefundOptionsView>;

/// <summary>
/// "Qaytar" / "Job-u ləğv et və qaytar" in the panel (plan section 2.5): full
/// or partial, a reason is mandatory, the destination per the matrix.
/// </summary>
public sealed record AdminRefundCommand(
    Guid AdminUserId, Guid PaymentId, decimal? Amount, string? Destination, string? Reason, bool CancelJob, string? IdempotencyKey, string? Ip)
    : IRequest<AdminRefundResult>;

/// <summary>
/// Step one of <see cref="AdminRefundCommand"/>, in one transaction: checked,
/// the job cancelled if asked, the refunds made and audited - so the next
/// request already sees them. Step two carries them out.
/// </summary>
public sealed record PlanAdminRefundCommand(AdminRefundCommand Request) : IRequest<AdminRefundResult>, ITransactionalRequest;

/// <summary>A failed refund tried again, the same way or to another destination (plan section 2.5).</summary>
public sealed record AdminRetryRefundCommand(Guid AdminUserId, Guid RefundId, string? Destination, string? Note, string? Ip)
    : IRequest<RefundView>, ITransactionalRequest;

public sealed record GetRefundQuery(Guid RefundId) : IRequest<RefundView>;

public sealed record ListRefundsQuery(IReadOnlyCollection<string>? Statuses, IReadOnlyCollection<string>? Reasons, Guid? PaymentId, int Page, int PageSize)
    : IRequest<RefundPage>;

/// <summary>The refund status page an anonymous customer opens from the QR (plan section 2.9: <c>neoprint.az/refund/{id}</c>).</summary>
public sealed record GetPublicRefundQuery(Guid RefundId) : IRequest<PublicRefundView>;

/// <summary>A voucher turned into wallet money (plan section 2.14: from then on it follows the wallet's rules).</summary>
public sealed record RedeemVoucherCommand(Guid UserId, string? Code) : IRequest<WalletView>, ITransactionalRequest;

/// <param name="ExternalDestinations">Where the bank or card part may go; empty when that part is fully refunded.</param>
public sealed record RefundOptionsView(
    Guid PaymentId,
    string Method,
    bool HasAccount,
    decimal Refundable,
    decimal ExternalRefundable,
    decimal WalletRefundable,
    int BonusPagesRefundable,
    IReadOnlyList<string> ExternalDestinations,
    bool JobCancellable);

public sealed record AdminRefundResult(Guid PaymentId, decimal Amount, string DedupKey, bool JobCancelled, IReadOnlyList<Guid> RefundIds);

public sealed record RefundView(
    Guid Id,
    Guid PaymentId,
    Guid? JobId,
    Guid? UserId,
    decimal Amount,
    int BonusPages,
    string Source,
    string Destination,
    string Reason,
    string InitiatedBy,
    string Status,
    int Attempts,
    DateTime? NextAttemptAt,
    string? LastError,
    string? Note,
    string? ProviderRefundRef,
    string? VoucherCode,
    RefundEvidence? Evidence,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record RefundPage(IReadOnlyList<RefundView> Items, long Total, int Page, int PageSize);

/// <param name="Destination">Card, Wallet or Voucher; never a card number or a name.</param>
public sealed record PublicRefundView(Guid Id, string Status, decimal Amount, string Currency, string Destination, DateTime CreatedAt, DateTime? CompletedAt, string? VoucherCode);

/// <summary>Refunds in the admin panel and for the customer.</summary>
public sealed class RefundAdminHandlers :
    IRequestHandler<GetRefundOptionsQuery, RefundOptionsView>,
    IRequestHandler<AdminRefundCommand, AdminRefundResult>,
    IRequestHandler<PlanAdminRefundCommand, AdminRefundResult>,
    IRequestHandler<AdminRetryRefundCommand, RefundView>,
    IRequestHandler<GetRefundQuery, RefundView>,
    IRequestHandler<ListRefundsQuery, RefundPage>,
    IRequestHandler<GetPublicRefundQuery, PublicRefundView>,
    IRequestHandler<RedeemVoucherCommand, WalletView>
{
    private static readonly PrintJobStatus[] Cancellable =
        [PrintJobStatus.PendingPayment, PrintJobStatus.Paid, PrintJobStatus.Queued, PrintJobStatus.Interrupted, PrintJobStatus.Suspected];

    private readonly ISender _sender;
    private readonly RefundPlanner _planner;
    private readonly IPaymentRepository _payments;
    private readonly IRefundRepository _refunds;
    private readonly IRefundVoucherRepository _vouchers;
    private readonly IPrintJobRepository _jobs;
    private readonly PrintQueueService _queue;
    private readonly IKioskNotifier _kiosks;
    private readonly WalletLedger _wallet;
    private readonly IAuditWriter _audit;
    private readonly IEventPublisher _events;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<RefundAdminHandlers> _logger;

    public RefundAdminHandlers(
        ISender sender,
        RefundPlanner planner,
        IPaymentRepository payments,
        IRefundRepository refunds,
        IRefundVoucherRepository vouchers,
        IPrintJobRepository jobs,
        PrintQueueService queue,
        IKioskNotifier kiosks,
        WalletLedger wallet,
        IAuditWriter audit,
        IEventPublisher events,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<RefundAdminHandlers> logger)
    {
        _sender = sender;
        _planner = planner;
        _payments = payments;
        _refunds = refunds;
        _vouchers = vouchers;
        _jobs = jobs;
        _queue = queue;
        _kiosks = kiosks;
        _wallet = wallet;
        _audit = audit;
        _events = events;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="GetRefundOptionsQuery" />
    public async Task<RefundOptionsView> Handle(GetRefundOptionsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var existing = await _refunds.ListForPaymentAsync(payment.Id, cancellationToken).ConfigureAwait(false);
        var parts = payment.WasPaid && payment.Purpose == PaymentPurpose.PrintJob ? payment.Parts : null;

        var external = parts is null ? Money.Zero : parts.External - Sum(existing, RefundSource.External);
        var wallet = parts is null ? Money.Zero : parts.Wallet - Sum(existing, RefundSource.Wallet);
        var bonus = parts is null ? 0 : parts.BonusPages - existing.Where(r => r.Source == RefundSource.Bonus).Sum(r => r.BonusPages);
        var job = payment.JobId is { } jobId ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) : null;

        return new RefundOptionsView(
            payment.Id,
            payment.Method.ToString(),
            payment.UserId is not null,
            (external + wallet).Amount,
            external.Amount,
            wallet.Amount,
            Math.Max(0, bonus),
            external.IsPositive ? RefundHandlers.AllowedExternal(payment).Select(d => d.ToString()).ToList() : [],
            job is not null && Cancellable.Contains(job.Status));
    }

    /// <inheritdoc cref="AdminRefundCommand" />
    public async Task<AdminRefundResult> Handle(AdminRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _sender.Send(new PlanAdminRefundCommand(request), cancellationToken).ConfigureAwait(false);

        foreach (var refundId in result.RefundIds)
        {
            // Carried out now; if the provider does not answer, the retry job goes on.
            await _sender.Send(new ProcessRefundCommand(refundId), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <inheritdoc cref="PlanAdminRefundCommand" />
    public async Task<AdminRefundResult> Handle(PlanAdminRefundCommand plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ValidationException([new ValidationError("reason", "refunds.reasonRequired")]);
        }

        if (request.Amount is { } asked && (asked <= 0 || decimal.Round(asked, 2) != asked))
        {
            throw new ValidationException([new ValidationError("amount", "refunds.amountInvalid")]);
        }

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");

        if (!payment.WasPaid || payment.Purpose != PaymentPurpose.PrintJob)
        {
            throw new DomainException("refunds.notRefundable");
        }

        var options = await Handle(new GetRefundOptionsQuery(payment.Id), cancellationToken).ConfigureAwait(false);

        if (options.Refundable <= 0 && options.BonusPagesRefundable <= 0)
        {
            throw new DomainException("refunds.nothingLeft");
        }

        if (request.Amount is { } amount && amount > options.Refundable)
        {
            throw new DomainException(UserMessage.Of("payments.refundExceedsPayment", ("refundable", options.Refundable), ("asked", amount)));
        }

        if (request.Destination is { } chosen && !options.ExternalDestinations.Contains(chosen))
        {
            throw new DomainException("refunds.destinationNotAllowed");
        }

        var now = _clock.UtcNow;
        var actor = ActorContext.Admin(request.AdminUserId, RefundHandlers.CancelledForRefund, _correlation.CorrelationId);
        var jobCancelled = false;

        if (request.CancelJob && payment.JobId is { } jobId
            && await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) is { } job
            && Cancellable.Contains(job.Status))
        {
            // Cancelled with the refund's own reason: the cancellation consumer
            // leaves the money to this decision rather than refunding it all again.
            await PrintJobCancellation
                .CancelAsync(job, new UserMessage(RefundHandlers.CancelledForRefund), actor, now, _jobs, _queue, _kiosks, _events, _correlation, cancellationToken)
                .ConfigureAwait(false);
            jobCancelled = true;
        }

        var dedupKey = $"admin:{request.IdempotencyKey ?? Guid.NewGuid().ToString("N")}";

        var refundIds = await _planner.PlanAsync(
                new RefundRequested(
                    payment.Id,
                    payment.JobId,
                    nameof(RefundReason.AdminManual),
                    request.Amount,
                    dedupKey,
                    _correlation.CorrelationId,
                    request.Destination,
                    request.Reason.Trim(),
                    request.AdminUserId,
                    nameof(ActorType.Admin)),
                cancellationToken)
            .ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "payments.refund",
                    nameof(Payment),
                    payment.Id.ToString(),
                    null,
                    System.Text.Json.JsonSerializer.Serialize(new { amount = request.Amount ?? options.Refundable, request.Destination, jobCancelled }),
                    request.Ip,
                    request.Reason.Trim()),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Admin {AdminId} refunded payment {PaymentId} ({DedupKey}).", request.AdminUserId, payment.Id, dedupKey);

        return new AdminRefundResult(payment.Id, request.Amount ?? options.Refundable, dedupKey, jobCancelled, refundIds);
    }

    /// <inheritdoc cref="AdminRetryRefundCommand" />
    public async Task<RefundView> Handle(AdminRetryRefundCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("refund");
        var payment = await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");

        var destination = request.Destination is { } chosen
            ? Enum.TryParse<RefundDestination>(chosen, out var parsed) ? parsed : throw new DomainException("refunds.destinationInvalid")
            : refund.Destination;

        // After a failed card or bank refund: a registered user's money may go
        // to the wallet, an anonymous customer's to a voucher (plan section 2.5).
        var allowed = refund.Source switch
        {
            RefundSource.External when payment.UserId is null => new[] { RefundDestination.OriginalMethod, RefundDestination.Voucher },
            RefundSource.External => [RefundDestination.OriginalMethod, RefundDestination.Wallet],
            _ => [RefundDestination.Wallet],
        };

        if (!allowed.Contains(destination))
        {
            throw new DomainException("refunds.destinationNotAllowed");
        }

        var before = refund.Destination;
        refund.Retry(destination, ActorContext.Admin(request.AdminUserId, "refunds.reason.retried", _correlation.CorrelationId), _clock.UtcNow);

        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            refund.Note = request.Note.Trim();
        }

        await _refunds.UpdateAsync(refund, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    before == destination ? "refunds.retry" : "refunds.changeDestination",
                    nameof(Refund),
                    refund.Id.ToString(),
                    before.ToString(),
                    destination.ToString(),
                    request.Ip,
                    request.Note?.Trim()),
                cancellationToken)
            .ConfigureAwait(false);

        return View(refund);
    }

    /// <inheritdoc cref="GetRefundQuery" />
    public async Task<RefundView> Handle(GetRefundQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return View(await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("refund"));
    }

    /// <inheritdoc cref="ListRefundsQuery" />
    public async Task<RefundPage> Handle(ListRefundsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var filter = new RefundFilter(
            request.Statuses?.Select(s => Enum.TryParse<RefundStatus>(s, out var v) ? (RefundStatus?)v : null).OfType<RefundStatus>().ToList(),
            request.Reasons?.Select(s => Enum.TryParse<RefundReason>(s, out var v) ? (RefundReason?)v : null).OfType<RefundReason>().ToList(),
            request.PaymentId);

        var (items, total) = await _refunds.SearchAsync(filter, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        return new RefundPage(items.Select(View).ToList(), total, page, size);
    }

    /// <inheritdoc cref="GetPublicRefundQuery" />
    public async Task<PublicRefundView> Handle(GetPublicRefundQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("refund");

        return new PublicRefundView(
            refund.Id,
            refund.Status.ToString(),
            refund.Amount.Amount,
            refund.Amount.Currency,
            refund.Destination switch
            {
                RefundDestination.OriginalMethod => "Card",
                _ => refund.Destination.ToString(),
            },
            refund.CreatedAt,
            refund.CompletedAt,
            refund.Destination == RefundDestination.Voucher ? refund.VoucherCode : null);
    }

    /// <inheritdoc cref="RedeemVoucherCommand" />
    public async Task<WalletView> Handle(RedeemVoucherCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var voucher = await _vouchers.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("voucher");
        var now = _clock.UtcNow;

        voucher.Redeem(request.UserId, now);
        await _vouchers.UpdateAsync(voucher, cancellationToken).ConfigureAwait(false);

        await _wallet
            .PostAsync(request.UserId, WalletTransactionType.VoucherRedeem, voucher.Amount, 0, voucher.Code, $"voucher:{voucher.Code}", request.UserId, now, null, cancellationToken)
            .ConfigureAwait(false);

        var wallet = await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        return new WalletView(wallet.Balance.Amount, wallet.BonusPages, wallet.Balance.Currency);
    }

    private static Money Sum(IEnumerable<Refund> refunds, RefundSource source)
        => refunds.Where(r => r.Source == source).Aggregate(Money.Zero, (sum, r) => sum + r.Amount);

    private static RefundView View(Refund refund)
        => new(
            refund.Id,
            refund.PaymentId,
            refund.JobId,
            refund.UserId,
            refund.Amount.Amount,
            refund.BonusPages,
            refund.Source.ToString(),
            refund.Destination.ToString(),
            refund.Reason.ToString(),
            refund.InitiatedBy.ToString(),
            refund.Status.ToString(),
            refund.Attempts,
            refund.NextAttemptAt,
            refund.LastError,
            refund.Note,
            refund.ProviderRefundRef,
            refund.VoucherCode,
            refund.Evidence,
            refund.CreatedAt,
            refund.CompletedAt);
}
