using NeoPrint.Domain.Common;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Domain.Tests.Refunds;

/// <summary>
/// The evidence rule for a partly printed job (the owner's rule of
/// 2026-09-30), the refund state machine and vouchers (plan section 2.5).
/// </summary>
public sealed class RefundTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext System = ActorContext.System();

    private static AttemptEvidence Counter(long before, long after, string unit = "impressions")
        => new(before, after, unit, "S-1", "Interrupted");

    [Fact]
    public void The_printers_counter_is_trusted_first()
    {
        var evidence = RefundEvidencePolicy.Decide(10, duplex: false, [Counter(500, 504)], kioskReportedSides: 4);

        evidence.Source.Should().Be(EvidenceSource.PrinterCounter);
        evidence.PrintedSides.Should().Be(4);
    }

    [Fact]
    public void When_the_sources_disagree_the_customer_gets_the_benefit_of_the_doubt()
    {
        // The counter says 6 came out, the kiosk only 3: the refund is for 7, not 4.
        var evidence = RefundEvidencePolicy.Decide(10, duplex: false, [Counter(500, 506)], kioskReportedSides: 3);

        evidence.Source.Should().Be(EvidenceSource.KioskJournal);
        evidence.PrintedSides.Should().Be(3);
        evidence.Detail.Should().Contain("PrinterCounter=6").And.Contain("KioskJournal=3");
    }

    [Fact]
    public void A_sheet_counter_on_a_two_sided_job_counts_two_sides_a_sheet()
    {
        RefundEvidencePolicy.Decide(10, duplex: true, [Counter(10, 13, "sheets")], null).PrintedSides.Should().Be(6);
    }

    [Fact]
    public void The_spooler_only_ever_says_all_of_it_and_loses_to_a_smaller_count()
    {
        var done = new AttemptEvidence(null, null, null, "S-2", "Completed");

        RefundEvidencePolicy.Decide(4, false, [done], null).Should().Match<RefundEvidence>(e => e.Source == EvidenceSource.Spooler && e.PrintedSides == 4);
        RefundEvidencePolicy.Decide(4, false, [done], 1).Source.Should().Be(EvidenceSource.KioskJournal);
    }

    [Fact]
    public void With_no_evidence_the_whole_amount_comes_back()
    {
        var evidence = RefundEvidencePolicy.Decide(4, false, [new AttemptEvidence(null, null, null, null, "Interrupted")], null);

        evidence.Source.Should().Be(EvidenceSource.None);
        RefundEvidencePolicy.Unprinted(Money.FromAzn(0.40m), evidence).Should().Be(Money.FromAzn(0.40m));
    }

    [Theory]
    [InlineData(0, 0.40)]
    [InlineData(1, 0.30)]
    [InlineData(3, 0.10)]
    [InlineData(4, 0)]
    [InlineData(9, 0)]
    public void The_refund_is_the_unprinted_share_of_what_was_paid(int printed, decimal back)
    {
        var evidence = new RefundEvidence(EvidenceSource.KioskJournal, printed, 4, string.Empty);

        RefundEvidencePolicy.Unprinted(Money.FromAzn(0.40m), evidence).Should().Be(Money.FromAzn(back));
    }

    [Fact]
    public void A_counter_that_went_backwards_or_is_missing_is_not_evidence()
    {
        RefundEvidencePolicy.Decide(4, false, [Counter(10, 8), new AttemptEvidence(10, null, "impressions", null, "Interrupted")], null)
            .Source.Should().Be(EvidenceSource.None);
    }

    [Fact]
    public void A_refund_moves_pending_processing_succeeded_and_a_failed_one_only_back_to_processing()
    {
        var refund = Refund.Create(Guid.NewGuid(), null, null, Money.FromAzn(0.2m), 0, RefundSource.External, RefundDestination.OriginalMethod,
            RefundReason.SystemError, ActorType.System, null, "k", Now);

        refund.Start(System, Now);
        refund.CountAttempt();
        refund.Fail("refunds.provider.bankRefused", retryAt: Now.AddMinutes(1), System, Now);
        refund.Status.Should().Be(RefundStatus.Processing, "a try with more to come keeps it ours");

        refund.Fail("refunds.provider.bankRefused", retryAt: null, System, Now);
        refund.Status.Should().Be(RefundStatus.Failed);

        refund.Retry(RefundDestination.Voucher, System, Now);
        refund.Status.Should().Be(RefundStatus.Processing);
        refund.Attempts.Should().Be(0, "an admin's retry starts a fresh set of tries");
        refund.NextAttemptAt.Should().Be(Now, "due at once");

        refund.Succeed(null, System, Now, "RF-AAAA-BBBB");
        refund.Status.Should().Be(RefundStatus.Succeeded);
        FluentActions.Invoking(() => refund.Retry(RefundDestination.Wallet, System, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void The_wallet_part_never_leaves_the_wallet_and_bonus_pages_come_back_as_pages()
    {
        FluentActions.Invoking(() => Refund.Create(Guid.NewGuid(), null, Guid.NewGuid(), Money.FromAzn(1m), 0, RefundSource.Wallet,
                RefundDestination.OriginalMethod, RefundReason.AdminManual, ActorType.Admin, null, "w", Now))
            .Should().Throw<DomainException>();

        FluentActions.Invoking(() => Refund.Create(Guid.NewGuid(), null, Guid.NewGuid(), Money.FromAzn(1m), 0, RefundSource.External,
                RefundDestination.Bonus, RefundReason.AdminManual, ActorType.Admin, null, "b", Now))
            .Should().Throw<DomainException>();

        var walletPart = Refund.Create(Guid.NewGuid(), null, Guid.NewGuid(), Money.FromAzn(1m), 0, RefundSource.Wallet,
            RefundDestination.Wallet, RefundReason.AdminManual, ActorType.Admin, null, "w2", Now);
        walletPart.Start(System, Now);
        walletPart.Fail("x", null, System, Now);

        FluentActions.Invoking(() => walletPart.Retry(RefundDestination.OriginalMethod, System, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void A_voucher_is_used_once_and_not_after_it_expires()
    {
        var voucher = RefundVoucher.Issue(Guid.NewGuid(), Guid.NewGuid(), Money.FromAzn(0.3m), Now, validDays: 90);
        voucher.Code.Should().MatchRegex("^RF-[A-Z2-9]{4}-[A-Z2-9]{4}$");

        var user = Guid.NewGuid();
        voucher.Redeem(user, Now.AddDays(1));

        FluentActions.Invoking(() => voucher.Redeem(Guid.NewGuid(), Now.AddDays(2)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("vouchers.alreadyUsed");

        var old = RefundVoucher.Issue(Guid.NewGuid(), Guid.NewGuid(), Money.FromAzn(0.3m), Now, validDays: 90);
        FluentActions.Invoking(() => old.Redeem(user, Now.AddDays(91)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("vouchers.expired");
    }
}
