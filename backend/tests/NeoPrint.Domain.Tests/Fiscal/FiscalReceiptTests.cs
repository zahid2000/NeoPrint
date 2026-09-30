using NeoPrint.Domain.Common;
using NeoPrint.Domain.Fiscal;

namespace NeoPrint.Domain.Tests.Fiscal;

/// <summary>The fiscal receipt's life (plan section 2.12).</summary>
public sealed class FiscalReceiptTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext System = ActorContext.System();

    private static FiscalReceipt Sale(decimal amount = 0.30m)
        => FiscalReceipt.Create(
            FiscalReceiptType.Sale,
            "sale:x",
            Money.FromAzn(amount),
            [new FiscalLine("Çap xidməti", 1, Money.FromAzn(amount), Money.FromAzn(amount))],
            [new FiscalPaymentPart("BankQr", Money.FromAzn(amount))],
            Now,
            terminalId: " T-1 ");

    [Fact]
    public void Nothing_paid_in_money_is_not_fiscalized()
    {
        FluentActions.Invoking(() => Sale(0m)).Should().Throw<DomainException>().Which.Text.Key.Should().Be("fiscal.nothingToFiscalize");
    }

    [Fact]
    public void A_new_receipt_is_due_at_once_and_a_try_holds_it_back_while_in_flight()
    {
        var receipt = Sale();
        receipt.TerminalId.Should().Be("T-1");
        receipt.NextAttemptAt.Should().Be(Now);

        receipt.CountAttempt(Now);
        receipt.Attempts.Should().Be(1);
        receipt.NextAttemptAt.Should().BeAfter(Now, "a parallel run must not ask the provider twice");
    }

    [Fact]
    public void A_passing_failure_waits_a_refusal_fails_and_an_admin_retry_starts_again()
    {
        var receipt = Sale();
        receipt.CountAttempt(Now);
        receipt.Fail("fiscal.provider.unavailable", Now.AddMinutes(1), System, Now);
        receipt.Status.Should().Be(FiscalReceiptStatus.Pending);
        receipt.NextAttemptAt.Should().Be(Now.AddMinutes(1));

        receipt.CountAttempt(Now);
        receipt.Fail("fiscal.provider.rejected", null, System, Now);
        receipt.Status.Should().Be(FiscalReceiptStatus.Failed);
        receipt.NextAttemptAt.Should().BeNull();

        receipt.Retry(System, Now.AddHours(1));
        receipt.Status.Should().Be(FiscalReceiptStatus.Pending);
        receipt.Attempts.Should().Be(0);
        receipt.NextAttemptAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void An_issued_receipt_stays_issued()
    {
        var receipt = Sale();
        receipt.CountAttempt(Now);
        receipt.Issue("N-1", "F-1", "https://check/F-1", System, Now);
        receipt.Issue("N-2", "F-2", null, System, Now);

        receipt.FiscalDocumentNumber.Should().Be("N-1", "the same answer twice changes nothing");
        receipt.Status.Should().Be(FiscalReceiptStatus.Issued);
        FluentActions.Invoking(() => receipt.Retry(System, Now)).Should().Throw<DomainException>().Which.Text.Key.Should().Be("fiscal.alreadyIssued");
        FluentActions.Invoking(() => receipt.CountAttempt(Now)).Should().Throw<DomainException>();
    }
}
