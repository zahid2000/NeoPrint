using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Operations;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Operations;

/// <summary>A paid payment, as the money check reads it.</summary>
/// <param name="Money">The wallet and card or bank part.</param>
/// <param name="Wallet">The part taken from the balance.</param>
public sealed record CheckedPayment(
    Guid Id, Guid? KioskId, PaymentPurpose Purpose, PaymentStatus Status, DateTime PaidAt, decimal Money, decimal Wallet, int BonusPages, Guid? JobId);

public sealed record CheckedRefund(Guid Id, Guid PaymentId, RefundStatus Status, RefundDestination Destination, decimal Amount, DateTime? CompletedAt);

/// <summary>What the money check reads, for payments paid and refunds completed since a moment.</summary>
/// <param name="Refunds">Every refund of those payments, and every refund completed since then.</param>
/// <param name="LedgerRefs">The <c>RefId</c> of every ledger row that points at one of those payments or refunds.</param>
/// <param name="FiscalPayments">Those payments that have a fiscal receipt, whatever its status.</param>
public sealed record MoneySnapshot(
    IReadOnlyList<CheckedPayment> Payments,
    IReadOnlyDictionary<Guid, PrintJobStatus> Jobs,
    IReadOnlyList<CheckedRefund> Refunds,
    IReadOnlySet<string> LedgerRefs,
    IReadOnlySet<Guid> FiscalPayments);

public interface IMoneySnapshotReader
{
    Task<MoneySnapshot> ReadAsync(DateTime since, CancellationToken cancellationToken = default);
}

/// <summary>A mismatch between payments, refunds, the ledger and the fiscal receipts.</summary>
/// <param name="Kind"><c>paidNotPrinted</c>, <c>refundWithoutLedger</c>, <c>walletPaymentWithoutLedger</c>, <c>paidWithoutFiscal</c>.</param>
/// <param name="EntityType"><c>Payment</c> or <c>Refund</c>.</param>
public sealed record MoneyFinding(string Kind, string EntityType, Guid EntityId, Guid PaymentId, Guid? KioskId, decimal Amount, IncidentSeverity Severity);

/// <summary>The rules, alone: no store, no clock (T-215).</summary>
public static class MoneyConsistency
{
    /// <summary>How long an order may wait between its payment and its refund before "paid, not printed" is a finding.</summary>
    public static readonly TimeSpan SettleGrace = TimeSpan.FromHours(1);

    public const string PaidNotPrinted = "paidNotPrinted";
    public const string RefundWithoutLedger = "refundWithoutLedger";
    public const string WalletPaymentWithoutLedger = "walletPaymentWithoutLedger";
    public const string PaidWithoutFiscal = "paidWithoutFiscal";

    /// <param name="fiscalGrace"><c>Fiscal.MaxPendingMinutes</c>: a receipt is being issued until then.</param>
    /// <param name="topUpsFiscalized"><c>Fiscal.TopUpReceiptMode</c> is <c>OnTopUp</c>.</param>
    public static IReadOnlyList<MoneyFinding> Find(MoneySnapshot snapshot, DateTime now, bool fiscalEnabled, bool topUpsFiscalized, TimeSpan fiscalGrace)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var findings = new List<MoneyFinding>();
        var refunded = snapshot.Refunds.Select(refund => refund.PaymentId).ToHashSet();

        foreach (var payment in snapshot.Payments)
        {
            // Paid for a print that will not come, and nothing went back.
            if (payment is { Purpose: PaymentPurpose.PrintJob, Status: PaymentStatus.Succeeded, JobId: { } job }
                && snapshot.Jobs.TryGetValue(job, out var status)
                && status is PrintJobStatus.Cancelled or PrintJobStatus.Expired
                && !refunded.Contains(payment.Id)
                && payment.PaidAt <= now - SettleGrace)
            {
                findings.Add(new(PaidNotPrinted, "Payment", payment.Id, payment.Id, payment.KioskId, payment.Money, IncidentSeverity.Critical));
            }

            // Taken from the balance, but the ledger has no debit.
            if ((payment.Wallet > 0 || payment.BonusPages > 0) && !snapshot.LedgerRefs.Contains(payment.Id.ToString()))
            {
                findings.Add(new(WalletPaymentWithoutLedger, "Payment", payment.Id, payment.Id, payment.KioskId, payment.Wallet, IncidentSeverity.Critical));
            }

            // A sale, or a top-up when top-ups carry the receipt, with no receipt at all.
            var needsReceipt = payment.Money > 0
                               && (payment.Purpose == PaymentPurpose.PrintJob || (payment.Purpose == PaymentPurpose.WalletTopUp && topUpsFiscalized));

            if (fiscalEnabled && needsReceipt && payment.PaidAt <= now - fiscalGrace && !snapshot.FiscalPayments.Contains(payment.Id))
            {
                findings.Add(new(PaidWithoutFiscal, "Payment", payment.Id, payment.Id, payment.KioskId, payment.Money, IncidentSeverity.High));
            }
        }

        foreach (var refund in snapshot.Refunds)
        {
            // Back to the balance, but the ledger has no credit.
            if (refund is { Status: RefundStatus.Succeeded, Destination: RefundDestination.Wallet or RefundDestination.Bonus }
                && !snapshot.LedgerRefs.Contains(refund.Id.ToString()))
            {
                var kiosk = snapshot.Payments.FirstOrDefault(payment => payment.Id == refund.PaymentId)?.KioskId;
                findings.Add(new(RefundWithoutLedger, "Refund", refund.Id, refund.PaymentId, kiosk, refund.Amount, IncidentSeverity.Critical));
            }
        }

        return findings;
    }
}

/// <summary>
/// The money check (plan section 6, "ödəniş reconciliation"; T-215): payments,
/// refunds, the wallet ledger and the fiscal receipts against each other, for
/// the last 30 days, hourly. Next to <c>PaymentReconciliationJob</c>, which
/// asks the provider about payments still open; this one asks our own records
/// whether they agree.
/// </summary>
/// <remarks>
/// Finds, never fixes: each mismatch is an incident (once while it is open)
/// and an audit entry; nothing is refunded, credited or fiscalized by it.
/// </remarks>
public sealed record RunPaymentConsistencyCheckCommand : IRequest<JobRun>;

public sealed class RunPaymentConsistencyCheckHandler : IRequestHandler<RunPaymentConsistencyCheckCommand, JobRun>
{
    /// <summary>How far back the check reads.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly IMoneySnapshotReader _reader;
    private readonly IncidentReporter _incidents;
    private readonly IAuditWriter _audit;
    private readonly IJobRunStore _runs;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<RunPaymentConsistencyCheckHandler> _logger;

    public RunPaymentConsistencyCheckHandler(
        IMoneySnapshotReader reader,
        IncidentReporter incidents,
        IAuditWriter audit,
        IJobRunStore runs,
        ISettingsReader settings,
        IClock clock,
        ILogger<RunPaymentConsistencyCheckHandler> logger)
    {
        _reader = reader;
        _incidents = incidents;
        _audit = audit;
        _runs = runs;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    public static string KeyOf(MoneyFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return $"money-check:{finding.Kind}:{finding.EntityId}";
    }

    public async Task<JobRun> Handle(RunPaymentConsistencyCheckCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _clock.UtcNow;
        var run = new JobRun { Job = JobNames.PaymentConsistency, StartedAt = now };
        var fiscalEnabled = await _settings.GetBoolAsync(SettingKeys.FiscalEnabled, true, cancellationToken).ConfigureAwait(false);
        var mode = await _settings.GetAsync(SettingKeys.FiscalTopUpReceiptMode, cancellationToken).ConfigureAwait(false) ?? "OnTopUp";
        var fiscalGrace = TimeSpan.FromMinutes(Math.Max(1, await _settings.GetIntAsync(SettingKeys.FiscalMaxPendingMinutes, 15, cancellationToken).ConfigureAwait(false)));

        var snapshot = await _reader.ReadAsync(now - Window, cancellationToken).ConfigureAwait(false);
        var findings = MoneyConsistency.Find(
            snapshot, now, fiscalEnabled, string.Equals(mode, "OnTopUp", StringComparison.OrdinalIgnoreCase), fiscalGrace);

        run.Counts["payments"] = snapshot.Payments.Count;
        run.Counts["refunds"] = snapshot.Refunds.Count;

        foreach (var kind in new[] { MoneyConsistency.PaidNotPrinted, MoneyConsistency.RefundWithoutLedger, MoneyConsistency.WalletPaymentWithoutLedger, MoneyConsistency.PaidWithoutFiscal })
        {
            run.Counts[kind] = findings.Count(finding => finding.Kind == kind);
        }

        foreach (var finding in findings)
        {
            run.Found(new JobFinding(finding.Kind, finding.PaymentId.ToString(), finding.Amount));
            NeoPrintMetrics.ReconciliationFindings.Add(
                1, new KeyValuePair<string, object?>("check", JobNames.PaymentConsistency), new KeyValuePair<string, object?>("kind", finding.Kind));

            var raised = await _incidents
                .RaiseAsync(
                    IncidentType.Other,
                    finding.Severity,
                    UserMessage.Of($"incidents.moneyCheck.{finding.Kind}", ("amount", finding.Amount), ("id", finding.PaymentId.ToString()[..8])),
                    RunPaymentConsistencyCheckHandler.KeyOf(finding),
                    cancellationToken,
                    finding.KioskId,
                    finding.EntityType,
                    finding.EntityId)
                .ConfigureAwait(false);

            if (raised is not null)
            {
                await _audit
                    .WriteAsync(
                        new AuditEntry(
                            null, $"reconciliation.{finding.Kind}", finding.EntityType, finding.EntityId.ToString(), null, null, null, Messages.Write("reconciliation.notCorrected"), ActorType.System),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        run.FinishedAt = _clock.UtcNow;
        await _runs.AddAsync(run, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payment consistency check: {Payments} payments, {Findings} finding(s).", snapshot.Payments.Count, findings.Count);

        return run;
    }
}
