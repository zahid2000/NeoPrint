using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Fiscal;
using NeoPrint.Application.Localization;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Receipts;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Application.Payments;

/// <summary>What the admin "Ödənişlər" page filters on (plan section 8A.3).</summary>
/// <param name="KioskIds">The kiosks the admin may see; null for every payment, top-ups included.</param>
/// <param name="Search">A payment id, a job code or the provider's reference.</param>
/// <param name="PaymentIds">Narrowed to these, for "refund gözləyir".</param>
public sealed record PaymentFilter(
    IReadOnlyCollection<Guid>? KioskIds,
    IReadOnlyCollection<PaymentStatus>? Statuses,
    IReadOnlyCollection<PaymentMethod>? Methods,
    PaymentChannel? Channel,
    PaymentPurpose? Purpose,
    DateTime? From,
    DateTime? To,
    bool? HadError,
    Guid? JobId,
    string? Search,
    IReadOnlyCollection<Guid>? PaymentIds = null);

/// <param name="Refund"><c>pending</c> for a refund on its way, <c>failed</c> for one that did not go through.</param>
public sealed record ListPaymentsQuery(
    Guid AdminUserId,
    IReadOnlyCollection<string>? Statuses,
    IReadOnlyCollection<string>? Methods,
    string? Channel,
    string? Purpose,
    Guid? KioskId,
    DateTime? From,
    DateTime? To,
    bool? HadError,
    string? Refund,
    string? Search,
    int Page,
    int PageSize) : IRequest<AdminPaymentPage>;

public sealed record GetAdminPaymentQuery(Guid AdminUserId, Guid PaymentId) : IRequest<AdminPaymentDetail>;

public sealed record AdminPaymentRow(
    Guid Id,
    string Status,
    string Method,
    string Channel,
    string Purpose,
    decimal Amount,
    decimal Refunded,
    string Currency,
    Guid? KioskId,
    string? KioskCode,
    Guid? JobId,
    string? JobCode,
    Guid? UserId,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? PaidAt);

public sealed record AdminPaymentPage(IReadOnlyList<AdminPaymentRow> Items, long Total, int Page, int PageSize);

/// <summary>Where the money came from (plan section 8A.3: "məbləğ bölgüsü").</summary>
/// <param name="Gross">The order's price snapshot: before the promo code and bonus pages.</param>
/// <param name="VolumeDiscount">What the profile's volume steps took off before the price was fixed.</param>
public sealed record PaymentSplit(
    decimal Gross,
    decimal VolumeDiscount,
    decimal PromoDiscount,
    string? PromoCode,
    decimal External,
    decimal Wallet,
    int BonusPages,
    decimal BonusValue);

public sealed record PaymentJobSummary(Guid Id, string JobCode, string Status, string? FileName, int Pages, int Copies, bool Color, string Paper);

public sealed record PaymentUserSummary(Guid Id, string PublicId, string Email);

public sealed record PaymentFiscalSummary(Guid Id, string Type, string Status, decimal Amount, string? FiscalId, string? TerminalId, string? LastError, DateTime? IssuedAt);

public sealed record PaymentRefundSummary(
    Guid Id,
    decimal Amount,
    int BonusPages,
    string Destination,
    string Status,
    string Reason,
    string InitiatedBy,
    int Attempts,
    string? LastError,
    string? VoucherCode,
    string? Note,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record PaymentTimelineEntry(DateTime At, string Event, string? Details);

/// <summary>The payment page (plan section 8A.3, "ödəniş detalı").</summary>
public sealed record AdminPaymentDetail(
    AdminPaymentRow Payment,
    PaymentSplit Split,
    decimal Refundable,
    string? Provider,
    string? ProviderRef,
    DateTime ExpiresAt,
    string? KioskName,
    PaymentJobSummary? Job,
    PaymentUserSummary? User,
    string? ReceiptNumber,
    IReadOnlyList<PaymentFiscalSummary> Fiscal,
    IReadOnlyList<PaymentRefundSummary> Refunds,
    IReadOnlyList<PaymentTimelineEntry> Timeline);

public sealed class AdminPaymentHandlers :
    IRequestHandler<ListPaymentsQuery, AdminPaymentPage>,
    IRequestHandler<GetAdminPaymentQuery, AdminPaymentDetail>
{
    private const int MaxPageSize = 100;

    /// <summary>"Refund gözləyir" looks at this many open refunds; more is a problem of its own.</summary>
    private const int OpenRefundLimit = 500;

    private readonly IPermissionResolver _permissions;
    private readonly IKioskRepository _kiosks;
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly IRefundRepository _refunds;
    private readonly IFiscalReceiptRepository _fiscal;
    private readonly IReceiptRepository _receipts;
    private readonly IUserRepository _users;

    public AdminPaymentHandlers(
        IPermissionResolver permissions,
        IKioskRepository kiosks,
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        IRefundRepository refunds,
        IFiscalReceiptRepository fiscal,
        IReceiptRepository receipts,
        IUserRepository users)
    {
        _permissions = permissions;
        _kiosks = kiosks;
        _payments = payments;
        _jobs = jobs;
        _refunds = refunds;
        _fiscal = fiscal;
        _receipts = receipts;
        _users = users;
    }

    /// <inheritdoc cref="ListPaymentsQuery" />
    public async Task<AdminPaymentPage> Handle(ListPaymentsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, MaxPageSize);
        var (scope, codes) = await ScopeAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        if (scope is { Count: 0 })
        {
            return new AdminPaymentPage([], 0, page, size);
        }

        var statuses = Parse<PaymentStatus>(request.Statuses, "status");
        IReadOnlyCollection<Guid>? paymentIds = null;

        switch (request.Refund?.Trim().ToLowerInvariant())
        {
            case null or "":
                break;
            case "failed":
                statuses = [PaymentStatus.RefundFailed];
                break;
            case "pending":
                paymentIds = await _refunds.ListOpenPaymentIdsAsync(OpenRefundLimit, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ValidationException([new ValidationError("refund", "payments.filterInvalid")]);
        }

        var filter = new PaymentFilter(
            scope,
            statuses,
            Parse<PaymentMethod>(request.Methods, "method"),
            ParseOne<PaymentChannel>(request.Channel, "channel"),
            ParseOne<PaymentPurpose>(request.Purpose, "purpose"),
            request.From,
            request.To,
            request.HadError,
            await JobBySearchAsync(request.Search, cancellationToken).ConfigureAwait(false),
            request.Search?.Trim(),
            paymentIds);

        var (items, total) = await _payments.SearchAsync(filter, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);
        var jobs = (await _jobs.ListByIdsAsync([.. items.Where(p => p.JobId is not null).Select(p => p.JobId!.Value)], cancellationToken).ConfigureAwait(false))
            .ToDictionary(job => job.Id, job => job.JobCode);

        return new AdminPaymentPage([.. items.Select(payment => Row(payment, codes, jobs))], total, page, size);
    }

    /// <inheritdoc cref="GetAdminPaymentQuery" />
    public async Task<AdminPaymentDetail> Handle(GetAdminPaymentQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");
        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        // Outside the admin's kiosks reads as not found, as a job does; a top-up
        // has no kiosk and belongs to admins who see every kiosk.
        if (payment.KioskId is { } kioskId ? !effective.CanSee(kioskId) : !effective.AllKiosks)
        {
            throw new NotFoundException("payment");
        }

        var kiosk = payment.KioskId is { } id ? await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) : null;
        var job = payment.JobId is { } jobId ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) : null;
        var user = payment.UserId is { } userId ? await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) : null;
        var receipt = await _receipts.FindByIdAsync(payment.Id, cancellationToken).ConfigureAwait(false);
        var fiscal = await _fiscal.ListForPaymentAsync(payment.Id, cancellationToken).ConfigureAwait(false);
        var refunds = await _refunds.ListForPaymentAsync(payment.Id, cancellationToken).ConfigureAwait(false);

        var codes = kiosk is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [kiosk.Id] = kiosk.Code };
        var jobCodes = job is null ? new Dictionary<Guid, string>() : new Dictionary<Guid, string> { [job.Id] = job.JobCode };
        var parts = payment.Parts;

        return new AdminPaymentDetail(
            Row(payment, codes, jobCodes),
            new PaymentSplit(
                payment.Gross.Amount,
                job?.Price.Price.VolumeDiscount ?? 0,
                payment.Discount.Amount,
                payment.PromoCode,
                parts?.External.Amount ?? 0,
                parts?.Wallet.Amount ?? 0,
                parts?.BonusPages ?? 0,
                parts?.BonusValue.Amount ?? 0),
            payment.Refundable.Amount,
            payment.Provider,
            payment.ProviderRef,
            payment.ExpiresAt,
            kiosk?.Name,
            job is null
                ? null
                : new PaymentJobSummary(job.Id, job.JobCode, job.Status.ToString(), job.LocalFileName, job.Price.Price.Pages, job.Options.Copies, job.Options.Color, job.Options.Paper),
            user is null ? null : new PaymentUserSummary(user.Id, user.PublicId, user.Email),
            receipt?.Number,
            [
                .. fiscal.OrderBy(f => f.CreatedAt).Select(f => new PaymentFiscalSummary(
                    f.Id, f.Type.ToString(), f.Status.ToString(), f.Amount.Amount, f.FiscalId, f.TerminalId, f.LastError, f.IssuedAt)),
            ],
            [
                .. refunds.OrderBy(r => r.CreatedAt).Select(r => new PaymentRefundSummary(
                    r.Id,
                    r.Amount.Amount,
                    r.BonusPages,
                    r.Destination.ToString(),
                    r.Status.ToString(),
                    r.Reason.ToString(),
                    r.InitiatedBy.ToString(),
                    r.Attempts,
                    Messages.WriteStored(r.LastError),
                    r.VoucherCode,
                    r.Note,
                    r.CreatedAt,
                    r.CompletedAt)),
            ],
            [.. payment.Timeline.OrderBy(e => e.At).Select(e => new PaymentTimelineEntry(e.At, e.Event, e.Details))]);
    }

    /// <summary>The kiosks the admin may see, and their codes; null scope means every payment.</summary>
    private async Task<(IReadOnlyCollection<Guid>? Scope, Dictionary<Guid, string> Codes)> ScopeAsync(
        Guid adminUserId, Guid? kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);
        var visible = kiosks.Where(kiosk => effective.CanSee(kiosk.Id)).ToDictionary(kiosk => kiosk.Id, kiosk => kiosk.Code);

        if (kioskId is { } one)
        {
            return (visible.ContainsKey(one) ? [one] : [], visible);
        }

        // Every kiosk: top-ups (no kiosk) are in too.
        return (effective.AllKiosks ? null : visible.Keys.ToList(), visible);
    }

    /// <summary>A six-digit job code typed in the search box finds its payments.</summary>
    private async Task<Guid?> JobBySearchAsync(string? search, CancellationToken cancellationToken)
    {
        var text = search?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length != 6 || !text.All(char.IsAsciiDigit))
        {
            return null;
        }

        var (found, _) = await _jobs.SearchAsync(new PrintJobFilter(null, null, null, null, text), 0, 1, cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault()?.Id;
    }

    private static AdminPaymentRow Row(Payment payment, IReadOnlyDictionary<Guid, string> kioskCodes, IReadOnlyDictionary<Guid, string> jobCodes) => new(
        payment.Id,
        payment.Status.ToString(),
        payment.Method.ToString(),
        payment.Channel.ToString(),
        payment.Purpose.ToString(),
        (payment.Parts?.Money ?? payment.Amount).Amount,
        payment.RefundedAmount.Amount,
        payment.Amount.Currency,
        payment.KioskId,
        payment.KioskId is { } kioskId && kioskCodes.TryGetValue(kioskId, out var code) ? code : null,
        payment.JobId,
        payment.JobId is { } jobId && jobCodes.TryGetValue(jobId, out var jobCode) ? jobCode : null,
        payment.UserId,
        Messages.WriteStored(payment.FailureReason),
        payment.CreatedAt,
        payment.PaidAt);

    private static IReadOnlyCollection<T>? Parse<T>(IReadOnlyCollection<string>? values, string field)
        where T : struct, Enum
    {
        if (values is not { Count: > 0 })
        {
            return null;
        }

        return [.. values.Select(value => Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ValidationException([new ValidationError(field, "payments.filterInvalid")]))];
    }

    private static T? ParseOne<T>(string? value, string field)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value)
            ? null
            : Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ValidationException([new ValidationError(field, "payments.filterInvalid")]);
}
