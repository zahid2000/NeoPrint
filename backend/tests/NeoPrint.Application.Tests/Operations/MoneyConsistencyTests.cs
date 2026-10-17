using NeoPrint.Application.Operations;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Application.Tests.Operations;

/// <summary>
/// The money check's rules (T-215): records that agree give nothing; each
/// kind of broken record gives its finding - and only that one.
/// </summary>
public sealed class MoneyConsistencyTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan FiscalGrace = TimeSpan.FromMinutes(15);

    private static CheckedPayment Payment(
        Guid? job = null, PaymentStatus status = PaymentStatus.Succeeded, decimal money = 0.40m, decimal wallet = 0, int bonus = 0,
        PaymentPurpose purpose = PaymentPurpose.PrintJob, TimeSpan? age = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), purpose, status, Now - (age ?? TimeSpan.FromHours(2)), money, wallet, bonus, job);

    private static MoneySnapshot Snapshot(
        IEnumerable<CheckedPayment> payments,
        IDictionary<Guid, PrintJobStatus>? jobs = null,
        IEnumerable<CheckedRefund>? refunds = null,
        IEnumerable<string>? ledger = null,
        IEnumerable<Guid>? fiscal = null)
        => new(
            [.. payments],
            new Dictionary<Guid, PrintJobStatus>(jobs ?? new Dictionary<Guid, PrintJobStatus>()),
            [.. refunds ?? []],
            (ledger ?? []).ToHashSet(StringComparer.Ordinal),
            (fiscal ?? []).ToHashSet());

    private static IReadOnlyList<MoneyFinding> Find(MoneySnapshot snapshot, bool fiscal = true)
        => MoneyConsistency.Find(snapshot, Now, fiscal, topUpsFiscalized: true, FiscalGrace);

    [Fact]
    public void Records_that_agree_give_nothing()
    {
        var job = Guid.NewGuid();
        var printed = Payment(job);
        var fromWallet = Payment(Guid.NewGuid(), money: 0.40m, wallet: 0.40m);
        var refundedOne = Payment(Guid.NewGuid(), status: PaymentStatus.Refunded);
        var refund = new CheckedRefund(Guid.NewGuid(), refundedOne.Id, RefundStatus.Succeeded, RefundDestination.Wallet, 0.40m, Now);

        var findings = Find(Snapshot(
            [printed, fromWallet, refundedOne],
            new Dictionary<Guid, PrintJobStatus> { [job] = PrintJobStatus.Completed, [fromWallet.JobId!.Value] = PrintJobStatus.Completed, [refundedOne.JobId!.Value] = PrintJobStatus.Cancelled },
            [refund],
            [fromWallet.Id.ToString(), refund.Id.ToString()],
            [printed.Id, fromWallet.Id, refundedOne.Id]));

        findings.Should().BeEmpty();
    }

    [Fact]
    public void Paid_for_an_order_that_was_cancelled_with_nothing_given_back_is_critical()
    {
        var job = Guid.NewGuid();
        var payment = Payment(job);

        var finding = Find(Snapshot([payment], new Dictionary<Guid, PrintJobStatus> { [job] = PrintJobStatus.Cancelled }, fiscal: [payment.Id]))
            .Should().ContainSingle().Subject;

        finding.Kind.Should().Be(MoneyConsistency.PaidNotPrinted);
        finding.Severity.Should().Be(IncidentSeverity.Critical);
    }

    [Fact]
    public void A_cancelled_order_within_the_hour_is_still_being_settled()
    {
        var job = Guid.NewGuid();
        var payment = Payment(job, age: TimeSpan.FromMinutes(30));

        Find(Snapshot([payment], new Dictionary<Guid, PrintJobStatus> { [job] = PrintJobStatus.Expired }, fiscal: [payment.Id])).Should().BeEmpty();
    }

    [Fact]
    public void A_refund_to_the_balance_without_its_ledger_entry_is_critical()
    {
        var payment = Payment(status: PaymentStatus.Refunded);
        var refund = new CheckedRefund(Guid.NewGuid(), payment.Id, RefundStatus.Succeeded, RefundDestination.Wallet, 0.40m, Now);

        var finding = Find(Snapshot([payment], refunds: [refund], fiscal: [payment.Id])).Should().ContainSingle().Subject;

        finding.Kind.Should().Be(MoneyConsistency.RefundWithoutLedger);
        finding.EntityId.Should().Be(refund.Id);
        finding.Severity.Should().Be(IncidentSeverity.Critical);
    }

    [Fact]
    public void A_refund_to_the_card_needs_no_ledger_entry()
    {
        var payment = Payment(status: PaymentStatus.Refunded);
        var refund = new CheckedRefund(Guid.NewGuid(), payment.Id, RefundStatus.Succeeded, RefundDestination.OriginalMethod, 0.40m, Now);

        Find(Snapshot([payment], refunds: [refund], fiscal: [payment.Id])).Should().BeEmpty();
    }

    [Fact]
    public void A_payment_from_the_balance_without_its_debit_is_critical()
    {
        var payment = Payment(wallet: 0.40m, bonus: 2);

        Find(Snapshot([payment], fiscal: [payment.Id])).Should().ContainSingle()
            .Which.Kind.Should().Be(MoneyConsistency.WalletPaymentWithoutLedger);
    }

    [Fact]
    public void A_sale_without_a_receipt_is_high_once_the_fiscal_grace_is_over_and_only_when_fiscalization_is_on()
    {
        var late = Payment(age: TimeSpan.FromMinutes(16));
        var fresh = Payment(age: TimeSpan.FromMinutes(14));
        var pagesOnly = Payment(money: 0, bonus: 3);

        var findings = Find(Snapshot([late, fresh, pagesOnly], ledger: [pagesOnly.Id.ToString()]));

        findings.Should().ContainSingle().Which.Should().Match<MoneyFinding>(f =>
            f.Kind == MoneyConsistency.PaidWithoutFiscal && f.PaymentId == late.Id && f.Severity == IncidentSeverity.High);
        Find(Snapshot([late]), fiscal: false).Should().BeEmpty();
    }

    [Fact]
    public void The_same_records_give_the_same_findings()
    {
        var job = Guid.NewGuid();
        var snapshot = Snapshot([Payment(job, wallet: 0.40m)], new Dictionary<Guid, PrintJobStatus> { [job] = PrintJobStatus.Cancelled });

        Find(snapshot).Should().BeEquivalentTo(Find(snapshot));
    }
}
