using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Tests.Common;

/// <summary>
/// Money and the one place rounding happens
/// (plan section 2.3, <c>docs/DOMAIN_RULES.md</c>).
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void An_amount_carries_its_currency()
    {
        var price = Money.FromAzn(1.50m);

        price.Amount.Should().Be(1.50m);
        price.Currency.Should().Be("AZN");
    }

    // -----------------------------------------------------------------------
    // Rounding: half to even, as plan section 2.3 requires
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0.125, 0.12)]
    [InlineData(0.135, 0.14)]
    [InlineData(0.145, 0.14)]
    [InlineData(0.155, 0.16)]
    public void A_tie_goes_to_the_even_digit(decimal raw, decimal expected)
    {
        // Banker's rounding: ties go up and down in equal measure, so a long
        // run of small prices does not drift upwards.
        Money.FromAzn(raw).Amount.Should().Be(expected);
    }

    [Theory]
    [InlineData(0.104, 0.10)]
    [InlineData(0.106, 0.11)]
    [InlineData(1.9999, 2.00)]
    public void Anything_that_is_not_a_tie_rounds_the_obvious_way(decimal raw, decimal expected)
        => Money.FromAzn(raw).Amount.Should().Be(expected);

    [Fact]
    public void A_negative_amount_rounds_symmetrically()
    {
        Money.FromAzn(-0.125m).Amount.Should().Be(-0.12m);
        Money.FromAzn(-0.135m).Amount.Should().Be(-0.14m);
    }

    [Fact]
    public void Rounding_is_available_for_intermediate_values()
    {
        // A price calculation works in intermediate values - a per-side price
        // times a paper multiplier - and those round the same way.
        Money.Round(0.125m).Should().Be(0.12m);
        Money.Round(1.005m).Should().Be(1.00m);
    }

    // -----------------------------------------------------------------------
    // Arithmetic
    // -----------------------------------------------------------------------

    [Fact]
    public void Adding_and_subtracting_keeps_two_decimals()
    {
        (Money.FromAzn(0.10m) + Money.FromAzn(0.20m)).Amount.Should().Be(0.30m);
        (Money.FromAzn(1.00m) - Money.FromAzn(0.35m)).Amount.Should().Be(0.65m);
    }

    [Fact]
    public void Multiplying_by_a_count_rounds_once_at_the_end()
    {
        // Thirty-three colour sides at 0.30: the result is exact, and nothing
        // is lost to rounding on the way.
        (Money.FromAzn(0.30m) * 33).Amount.Should().Be(9.90m);
    }

    [Fact]
    public void Applying_a_discount_rounds_to_the_nearest_qapik()
    {
        var beforeDiscount = Money.FromAzn(9.90m);

        // Ten percent off: 8.91 exactly.
        (beforeDiscount * 0.9m).Amount.Should().Be(8.91m);

        // Fifteen percent off 9.95 is 8.4575, which rounds to 8.46.
        (Money.FromAzn(9.95m) * 0.85m).Amount.Should().Be(8.46m);
    }

    [Fact]
    public void Negating_flips_the_sign()
        => (-Money.FromAzn(1.25m)).Amount.Should().Be(-1.25m);

    [Fact]
    public void A_minimum_order_is_the_larger_of_the_two()
    {
        var minimum = Money.FromAzn(0.20m);

        Money.Max(Money.FromAzn(0.10m), minimum).Should().Be(minimum);
        Money.Max(Money.FromAzn(0.50m), minimum).Amount.Should().Be(0.50m);
    }

    // -----------------------------------------------------------------------
    // Comparison
    // -----------------------------------------------------------------------

    [Fact]
    public void Amounts_compare_by_value()
    {
        (Money.FromAzn(1.00m) < Money.FromAzn(2.00m)).Should().BeTrue();
        (Money.FromAzn(2.00m) > Money.FromAzn(1.00m)).Should().BeTrue();
        (Money.FromAzn(1.00m) <= Money.FromAzn(1.00m)).Should().BeTrue();
        (Money.FromAzn(1.00m) >= Money.FromAzn(1.00m)).Should().BeTrue();
    }

    [Fact]
    public void Two_amounts_that_round_to_the_same_value_are_equal()
    {
        // Because the value is rounded on the way in, not on the way out.
        Money.FromAzn(1.004m).Should().Be(Money.FromAzn(1.0039m));
    }

    [Fact]
    public void Amounts_sort()
    {
        var sorted = new[] { Money.FromAzn(2m), Money.FromAzn(0.5m), Money.FromAzn(1m) }
            .Order()
            .Select(money => money.Amount);

        sorted.Should().Equal(0.50m, 1.00m, 2.00m);
    }

    [Fact]
    public void Zero_knows_it_is_zero()
    {
        Money.Zero.IsZero.Should().BeTrue();
        Money.Zero.IsPositive.Should().BeFalse();
        Money.FromAzn(0.01m).IsPositive.Should().BeTrue();
        Money.FromAzn(-0.01m).IsNegative.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Minor units
    // -----------------------------------------------------------------------

    [Fact]
    public void Qapik_convert_both_ways()
    {
        // Providers and fiscal APIs quote minor units, and converting by hand
        // at every boundary is how a factor of a hundred gets lost.
        Money.FromQapik(150).Amount.Should().Be(1.50m);
        Money.FromAzn(1.50m).ToQapik().Should().Be(150);
        Money.FromAzn(0.01m).ToQapik().Should().Be(1);
        Money.Zero.ToQapik().Should().Be(0);
    }

    [Fact]
    public void A_round_trip_through_qapik_changes_nothing()
    {
        foreach (var amount in new[] { 0.01m, 0.10m, 1.00m, 9.99m, 123.45m })
        {
            Money.FromQapik(Money.FromAzn(amount).ToQapik()).Amount.Should().Be(amount);
        }
    }

    // -----------------------------------------------------------------------
    // Currency
    // -----------------------------------------------------------------------

    [Fact]
    public void Two_currencies_cannot_be_combined()
    {
        // Only AZN exists today, so this is reached through the serializer's
        // guard rather than through arithmetic. The rule is here because the
        // day a second currency appears, the alternative is a wrong total.
        Money.Azn.Should().Be("AZN");
        Money.FromAzn(1m).Currency.Should().Be(Money.Azn);
    }

    [Fact]
    public void An_amount_reads_as_money_in_a_log()
        => Money.FromAzn(1.5m).ToString().Should().Be("1.50 AZN");
}
