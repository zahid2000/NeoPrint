using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;

namespace NeoPrint.Domain.Tests.Payments;

/// <summary>The payment state machine and its money rules (docs/STATE_MACHINES.md, "Payment"; docs/PAYMENT_FLOWS.md).</summary>
public sealed class PaymentTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext System = ActorContext.System();
    private static readonly Money Price = Money.FromAzn(0.30m);

    private static Payment Open(PaymentMethod method = PaymentMethod.BankQr)
        => Payment.Open(Guid.NewGuid(), Guid.NewGuid(), method, PaymentChannel.Kiosk, Price, Now, Now.AddMinutes(5));

    [Fact]
    public void A_payment_is_opened_for_a_positive_amount_that_expires_in_the_future()
    {
        var payment = Open();

        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.Amount.Should().Be(Price);
        payment.Refundable.Should().Be(Money.Zero, "nothing has been paid yet");
        payment.Timeline.Should().ContainSingle(e => e.Event == "created");

        FluentActions.Invoking(() => Payment.Open(Guid.NewGuid(), null, PaymentMethod.BankQr, PaymentChannel.Kiosk, Money.Zero, Now, Now.AddMinutes(5)))
            .Should().Throw<DomainException>();
        FluentActions.Invoking(() => Payment.Open(Guid.NewGuid(), null, PaymentMethod.BankQr, PaymentChannel.Kiosk, Price, Now, Now))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void The_money_is_taken_once_however_many_times_the_bank_says_so()
    {
        var payment = Open();

        payment.Succeed(PaymentParts.ExternalOnly(Price), PaymentMethod.BankQr, Now, System, Now).Should().BeTrue();
        payment.Succeed(PaymentParts.ExternalOnly(Price), PaymentMethod.BankQr, Now, System, Now.AddSeconds(1)).Should().BeFalse();

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.PendingTransitions.Should().ContainSingle(t => t.ToStatus == nameof(PaymentStatus.Succeeded));
        payment.Timeline.Count(e => e.Event == "succeeded").Should().Be(1);
        payment.Refundable.Should().Be(Price);
    }

    [Fact]
    public void A_bank_that_reports_another_amount_is_not_believed()
    {
        var payment = Open();

        FluentActions.Invoking(() => payment.Succeed(PaymentParts.ExternalOnly(Money.FromAzn(0.20m)), PaymentMethod.BankQr, Now, System, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("payments.amountMismatch");

        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.WasPaid.Should().BeFalse();
    }

    [Fact]
    public void A_confirmation_after_expiry_is_recorded_as_money_taken()
    {
        var payment = Open();
        payment.Expire(System, Now.AddMinutes(5));

        payment.Succeed(PaymentParts.ExternalOnly(Price), PaymentMethod.BankQr, Now.AddMinutes(4), System, Now.AddMinutes(6)).Should().BeTrue();

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.Timeline.Should().Contain(e => e.Event == "succeededAfterExpiry");
    }

    [Fact]
    public void A_declined_or_expired_payment_cannot_be_refunded_and_a_failed_one_stays_failed()
    {
        var declined = Open();
        declined.Fail("payments.declined", System, Now);

        FluentActions.Invoking(() => declined.ApplyRefund(Price, System, Now)).Should().Throw<DomainException>();
        FluentActions.Invoking(() => declined.Expire(System, Now)).Should().Throw<IllegalStateTransitionException>();
    }

    [Fact]
    public void Refunds_can_never_add_up_to_more_than_was_paid()
    {
        var payment = Open();
        payment.Succeed(PaymentParts.ExternalOnly(Price), PaymentMethod.BankQr, Now, System, Now);

        payment.ApplyRefund(Money.FromAzn(0.10m), System, Now);
        payment.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        payment.Refundable.Should().Be(Money.FromAzn(0.20m));

        FluentActions.Invoking(() => payment.ApplyRefund(Money.FromAzn(0.21m), System, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("payments.refundExceedsPayment");

        payment.ApplyRefund(Money.FromAzn(0.20m), System, Now);
        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.Refundable.Should().Be(Money.Zero);

        FluentActions.Invoking(() => payment.ApplyRefund(Money.FromAzn(0.01m), System, Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void The_app_qr_is_the_payments_own_deep_link_and_only_for_app_payments()
    {
        var app = Open(PaymentMethod.MobileApp);
        app.ShowAppQr(Now);
        app.QrContent.Should().Be($"neoprint://pay/{app.Id}");

        FluentActions.Invoking(() => Open().ShowAppQr(Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void A_payment_is_tied_to_one_bank_transaction_and_one_user()
    {
        var payment = Open();
        payment.AttachProvider("MockBank", "MB-1", "MOCKBANK:x", Now);

        FluentActions.Invoking(() => payment.AttachProvider("MockBank", "MB-2", null, Now)).Should().Throw<DomainException>();

        var user = Guid.NewGuid();
        payment.AssignUser(user, Now);
        payment.AssignUser(user, Now);

        FluentActions.Invoking(() => payment.AssignUser(Guid.NewGuid(), Now)).Should().Throw<DomainException>();
    }
}
