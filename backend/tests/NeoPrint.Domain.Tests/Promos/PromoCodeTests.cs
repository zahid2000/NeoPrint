using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Promos;

namespace NeoPrint.Domain.Tests.Promos;

/// <summary>Promo codes (plan section 2.7) and the payment's discount.</summary>
public sealed class PromoCodeTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static PromoCode Promo(PromoKind kind = PromoKind.Percent, decimal value = 20, int? total = null, decimal min = 0, Guid? kiosk = null)
        => PromoCode.Create("TELEBE-20", kind, value, Now.AddDays(-1), Now.AddDays(1), total, null, min,
            kiosk is { } k ? [k] : null, null, null, Now);

    [Fact]
    public void A_code_is_upper_case_letters_digits_and_dashes()
    {
        PromoCode.Normalize(" yay-2026 ").Should().Be("YAY-2026");
        FluentActions.Invoking(() => PromoCode.Normalize("ab")).Should().Throw<DomainException>().Which.Text.Key.Should().Be("promos.codeFormat");
        FluentActions.Invoking(() => PromoCode.Normalize("yay 2026")).Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(PromoKind.Percent, 20, 0.30, 0.06)]
    [InlineData(PromoKind.Percent, 100, 0.30, 0.30)]
    [InlineData(PromoKind.Fixed, 0.50, 0.30, 0.30)]
    [InlineData(PromoKind.Fixed, 0.10, 0.30, 0.10)]
    public void The_discount_is_a_share_or_a_sum_and_never_more_than_is_left(PromoKind kind, decimal value, decimal amount, decimal discount)
        => Promo(kind, value).Discount(Money.FromAzn(amount)).Should().Be(Money.FromAzn(discount));

    [Fact]
    public void It_says_why_it_does_not_apply()
    {
        var kiosk = Guid.NewGuid();
        var promo = Promo(total: 1, min: 1m, kiosk: kiosk);

        promo.Refusal(Now, kiosk, null, Money.FromAzn(0.50m))!.Key.Should().Be("promos.belowMinimum");
        promo.Refusal(Now, Guid.NewGuid(), null, Money.FromAzn(2m))!.Key.Should().Be("promos.notHere");
        promo.Refusal(Now.AddDays(-2), kiosk, null, Money.FromAzn(2m))!.Key.Should().Be("promos.notStarted");
        promo.Refusal(Now.AddDays(2), kiosk, null, Money.FromAzn(2m))!.Key.Should().Be("promos.expired");
        promo.Refusal(Now, kiosk, null, Money.FromAzn(2m)).Should().BeNull();

        promo.Deactivate();
        promo.Refusal(Now, kiosk, null, Money.FromAzn(2m))!.Key.Should().Be("promos.inactive");
    }

    [Fact]
    public void Its_terms_are_checked()
    {
        FluentActions.Invoking(() => Promo(PromoKind.Percent, 120)).Should().Throw<DomainException>().Which.Text.Key.Should().Be("promos.percentRange");
        FluentActions.Invoking(() => Promo(PromoKind.Fixed, 0.001m)).Should().Throw<DomainException>().Which.Text.Key.Should().Be("promos.amountInvalid");
        FluentActions.Invoking(() => PromoCode.Create("YAY-1", PromoKind.Fixed, 1, Now, Now, null, null, 0, null, null, null, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("promos.periodInvalid");
    }

    [Fact]
    public void A_usage_gives_its_place_back_once_and_a_late_payment_takes_it_again()
    {
        var usage = new PromoUsage { PromoCodeId = Guid.NewGuid(), PaymentId = Guid.NewGuid(), Discount = Money.FromAzn(0.1m) };

        usage.Release(refunded: false, Now).Should().BeTrue();
        usage.Release(refunded: false, Now).Should().BeFalse("given back already");
        usage.Status.Should().Be(PromoUsageStatus.Released);

        usage.Confirm();
        usage.Status.Should().Be(PromoUsageStatus.Confirmed, "a late callback paid it at the discounted price");

        usage.Release(refunded: true, Now).Should().BeTrue();
        usage.Status.Should().Be(PromoUsageStatus.Cancelled);
        usage.Confirm();
        usage.Status.Should().Be(PromoUsageStatus.Cancelled, "a refunded usage stays cancelled");
    }

    [Fact]
    public void A_payment_keeps_its_price_and_takes_the_discount_off_it_until_money_is_set_aside()
    {
        var payment = Payment.Open(Guid.NewGuid(), Guid.NewGuid(), PaymentMethod.MobileApp, PaymentChannel.Kiosk, Money.FromAzn(0.50m), Now, Now.AddMinutes(5));
        var promo = Guid.NewGuid();

        payment.ApplyDiscount(promo, "YAY-1", Money.FromAzn(0.20m), Now);
        payment.Amount.Should().Be(Money.FromAzn(0.30m));
        payment.Gross.Should().Be(Money.FromAzn(0.50m), "the snapshot price is never recomputed");

        // A new try works it out again on the same price.
        payment.ApplyDiscount(promo, "YAY-1", Money.FromAzn(0.10m), Now);
        payment.Amount.Should().Be(Money.FromAzn(0.40m));

        payment.ApplyDiscount(promo, "YAY-1", Money.Zero, Now);
        payment.Amount.Should().Be(Money.FromAzn(0.50m));
        payment.PromoCode.Should().BeNull();

        FluentActions.Invoking(() => payment.ApplyDiscount(promo, "YAY-1", Money.FromAzn(0.60m), Now)).Should().Throw<DomainException>();

        payment.Reserve(new PaymentParts(Money.FromAzn(0.50m), Money.Zero, 0, Money.Zero), PaymentMethod.Card, Now);
        FluentActions.Invoking(() => payment.ApplyDiscount(promo, "YAY-1", Money.FromAzn(0.10m), Now))
            .Should().Throw<DomainException>("money is being taken for the price as it is");
    }
}
