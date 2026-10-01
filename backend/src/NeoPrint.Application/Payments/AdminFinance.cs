using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Fiscal;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Application.Payments;

public sealed record ListVouchersQuery(int Page, int PageSize) : IRequest<VoucherPage>;

/// <param name="Status"><c>Active</c>, <c>Redeemed</c> or <c>Expired</c>.</param>
public sealed record VoucherRow(
    Guid Id,
    string Code,
    decimal Amount,
    string Currency,
    string Status,
    Guid PaymentId,
    Guid RefundId,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    Guid? RedeemedBy,
    DateTime? RedeemedAt);

public sealed record VoucherPage(IReadOnlyList<VoucherRow> Items, long Total, int Page, int PageSize);

/// <summary>
/// The money figures (plan section 8A.3: dashboard revenue, "gəlir hesabatı").
/// </summary>
/// <param name="From">First Baku day, included.</param>
/// <param name="To">Last Baku day, included.</param>
public sealed record GetRevenueQuery(Guid AdminUserId, DateOnly From, DateOnly To, Guid? KioskId) : IRequest<RevenueReport>;

public sealed record GetPaymentsDashboardQuery(Guid AdminUserId) : IRequest<PaymentsDashboard>;

/// <summary>
/// What printing earned: the money part of print payments (wallet and card
/// or bank), less what went back. Top-ups are money in, not revenue - they
/// are the customer's until spent - and are shown on their own; bonus pages
/// are worth nothing in money (plan sections 2.12, 2.14).
/// </summary>
public sealed record RevenueTotals(decimal Sales, int Payments, decimal Refunds, decimal Net, decimal Discounts, decimal TopUps);

public sealed record RevenueDay(DateOnly Day, decimal Sales, decimal Refunds, decimal Net, int Payments);

public sealed record RevenueByMethod(string Method, decimal Sales, int Payments);

public sealed record RevenueByKiosk(Guid KioskId, string? KioskCode, decimal Sales, int Payments);

public sealed record RevenueReport(
    DateOnly From,
    DateOnly To,
    string Currency,
    RevenueTotals Totals,
    IReadOnlyList<RevenueDay> Days,
    IReadOnlyList<RevenueByMethod> Methods,
    IReadOnlyList<RevenueByKiosk> Kiosks);

/// <summary>The dashboard's money card: today, the last seven days, and what needs an admin.</summary>
public sealed record PaymentsDashboard(
    RevenueTotals Today,
    IReadOnlyList<RevenueDay> LastSevenDays,
    long FailedRefunds,
    long FailedFiscal,
    long OpenMoneyIncidents);

public sealed class AdminFinanceHandlers :
    IRequestHandler<ListVouchersQuery, VoucherPage>,
    IRequestHandler<GetRevenueQuery, RevenueReport>,
    IRequestHandler<GetPaymentsDashboardQuery, PaymentsDashboard>
{
    /// <summary>A report covers at most a year: longer is a database export, not a page.</summary>
    private const int MaxDays = 366;

    private readonly IRefundVoucherRepository _vouchers;
    private readonly IPaymentRepository _payments;
    private readonly IRefundRepository _refunds;
    private readonly IFiscalReceiptRepository _fiscal;
    private readonly IIncidentRepository _incidents;
    private readonly IPermissionResolver _permissions;
    private readonly IKioskRepository _kiosks;
    private readonly IClock _clock;

    public AdminFinanceHandlers(
        IRefundVoucherRepository vouchers,
        IPaymentRepository payments,
        IRefundRepository refunds,
        IFiscalReceiptRepository fiscal,
        IIncidentRepository incidents,
        IPermissionResolver permissions,
        IKioskRepository kiosks,
        IClock clock)
    {
        _vouchers = vouchers;
        _payments = payments;
        _refunds = refunds;
        _fiscal = fiscal;
        _incidents = incidents;
        _permissions = permissions;
        _kiosks = kiosks;
        _clock = clock;
    }

    /// <inheritdoc cref="ListVouchersQuery" />
    public async Task<VoucherPage> Handle(ListVouchersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var now = _clock.UtcNow;
        var (items, total) = await _vouchers.ListAsync((page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        return new VoucherPage(
            [
                .. items.Select(voucher => new VoucherRow(
                    voucher.Id,
                    voucher.Code,
                    voucher.Amount.Amount,
                    voucher.Amount.Currency,
                    voucher.RedeemedAt is not null ? "Redeemed" : voucher.ExpiresAt <= now ? "Expired" : "Active",
                    voucher.PaymentId,
                    voucher.RefundId,
                    voucher.CreatedAt,
                    voucher.ExpiresAt,
                    voucher.RedeemedBy,
                    voucher.RedeemedAt)),
            ],
            total,
            page,
            size);
    }

    /// <inheritdoc cref="GetRevenueQuery" />
    public async Task<RevenueReport> Handle(GetRevenueQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.To < request.From || request.To.DayNumber - request.From.DayNumber >= MaxDays)
        {
            throw new ValidationException([new ValidationError("to", UserMessage.Of("reports.periodInvalid", ("max", MaxDays)))]);
        }

        return await ReportAsync(request.AdminUserId, request.From, request.To, request.KioskId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetPaymentsDashboardQuery" />
    public async Task<PaymentsDashboard> Handle(GetPaymentsDashboardQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var today = DateOnly.FromDateTime(BakuTime.From(_clock.UtcNow));
        var week = await ReportAsync(request.AdminUserId, today.AddDays(-6), today, null, cancellationToken).ConfigureAwait(false);
        var todayFigures = week.Days.Single(day => day.Day == today);

        return new PaymentsDashboard(
            new RevenueTotals(todayFigures.Sales, todayFigures.Payments, todayFigures.Refunds, todayFigures.Net, 0, 0),
            week.Days,
            await _refunds.CountAsync(RefundStatus.Failed, cancellationToken).ConfigureAwait(false),
            await _fiscal.CountAsync(FiscalReceiptStatus.Failed, cancellationToken).ConfigureAwait(false),
            await _incidents.CountOpenAsync(IncidentType.RefundFailed, cancellationToken).ConfigureAwait(false)
                + await _incidents.CountOpenAsync(IncidentType.FiscalFailed, cancellationToken).ConfigureAwait(false)
                + await _incidents.CountOpenAsync(IncidentType.JobSuspected, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RevenueReport> ReportAsync(Guid adminUserId, DateOnly from, DateOnly to, Guid? kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(kiosk => effective.CanSee(kiosk.Id))
            .ToDictionary(kiosk => kiosk.Id, kiosk => kiosk.Code);

        // Null: every payment, top-ups included - only for admins who see every kiosk.
        IReadOnlyCollection<Guid>? scope = kioskId is { } one
            ? kiosks.ContainsKey(one) ? [one] : []
            : effective.AllKiosks ? null : kiosks.Keys.ToList();

        var fromUtc = Utc(from);
        var toUtc = Utc(to.AddDays(1));
        var paid = scope is { Count: 0 } ? [] : await _payments.ListPaidAsync(fromUtc, toUtc, scope, cancellationToken).ConfigureAwait(false);
        var sales = paid.Where(p => p.Purpose == PaymentPurpose.PrintJob).ToList();

        var refunds = new List<(DateOnly Day, decimal Amount)>();
        var known = paid.ToDictionary(p => p.Id);

        foreach (var refund in await _refunds.ListCompletedAsync(fromUtc, toUtc, cancellationToken).ConfigureAwait(false))
        {
            var payment = known.GetValueOrDefault(refund.PaymentId)
                          ?? await _payments.FindByIdAsync(refund.PaymentId, cancellationToken).ConfigureAwait(false);

            // Only refunds of print payments the admin may see.
            if (payment is { Purpose: PaymentPurpose.PrintJob }
                && (scope is null || (payment.KioskId is { } k && scope.Contains(k))))
            {
                refunds.Add((Day(refund.CompletedAt!.Value), refund.Amount.Amount));
            }
        }

        static decimal Money(Payment payment) => (payment.Parts?.Money ?? payment.Amount).Amount;

        var days = new List<RevenueDay>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var daySales = sales.Where(p => Day(p.PaidAt!.Value) == day).ToList();
            var salesSum = daySales.Sum(Money);
            var refundSum = refunds.Where(r => r.Day == day).Sum(r => r.Amount);
            days.Add(new RevenueDay(day, salesSum, refundSum, salesSum - refundSum, daySales.Count));
        }

        var totalSales = sales.Sum(Money);
        var totalRefunds = refunds.Sum(r => r.Amount);

        return new RevenueReport(
            from,
            to,
            "AZN",
            new RevenueTotals(
                totalSales,
                sales.Count,
                totalRefunds,
                totalSales - totalRefunds,
                sales.Sum(p => p.Discount.Amount),
                paid.Where(p => p.Purpose == PaymentPurpose.WalletTopUp).Sum(Money)),
            days,
            [
                .. sales.GroupBy(p => p.Method)
                    .Select(g => new RevenueByMethod(g.Key.ToString(), g.Sum(Money), g.Count()))
                    .OrderByDescending(m => m.Sales),
            ],
            [
                .. sales.Where(p => p.KioskId is not null)
                    .GroupBy(p => p.KioskId!.Value)
                    .Select(g => new RevenueByKiosk(g.Key, kiosks.GetValueOrDefault(g.Key), g.Sum(Money), g.Count()))
                    .OrderByDescending(k => k.Sales),
            ]);
    }

    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(BakuTime.From(utc));

    /// <summary>The UTC moment a Baku day starts.</summary>
    private static DateTime Utc(DateOnly day)
        => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), BakuTime.Zone);
}
