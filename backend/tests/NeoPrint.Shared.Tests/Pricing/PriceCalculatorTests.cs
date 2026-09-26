using NeoPrint.Pricing;

namespace NeoPrint.Shared.Tests.Pricing;

/// <summary>
/// The price formula (plan section 2.3), with the default profile's values.
/// </summary>
/// <remarks>
/// Each expected figure is worked out by hand in the test, so a change to the
/// formula shows up as a disagreement with arithmetic anybody can check, not
/// with a number copied from the code's own output.
/// </remarks>
public sealed class PriceCalculatorTests
{
    /// <summary>The default profile from the plan.</summary>
    private static PriceTable Default() => new(
        BwPerSide: 0.10m,
        ColorPerSide: 0.30m,
        PaperMultipliers: new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = 2m },
        MinOrder: 0.20m,
        Tiers: [new PriceTier(50, 10m), new PriceTier(100, 15m)]);

    private static PrintSpec Spec(
        int pages, int copies = 1, bool duplex = false, bool color = false, string paper = "A4", int perSheet = 1)
        => new(pages, perSheet, copies, duplex, color, paper);

    [Fact]
    public void Ten_black_and_white_pages_cost_a_dime_each()
    {
        var price = PriceCalculator.Quote(Default(), Spec(10));

        price.Sides.Should().Be(10);
        price.Sheets.Should().Be(10);
        price.BasePrice.Should().Be(1.00m);
        price.FinalPrice.Should().Be(1.00m);
        price.MinimumApplied.Should().BeFalse();
    }

    [Fact]
    public void Colour_and_a3_multiply()
    {
        // 4 sides * 0.30 * 2 = 2.40
        PriceCalculator.Quote(Default(), Spec(4, color: true, paper: "A3")).FinalPrice.Should().Be(2.40m);
    }

    [Fact]
    public void A_single_page_is_charged_the_minimum_order()
    {
        var price = PriceCalculator.Quote(Default(), Spec(1));

        // 0.10 is below the 0.20 minimum.
        price.BasePrice.Should().Be(0.10m);
        price.FinalPrice.Should().Be(0.20m);
        price.MinimumApplied.Should().BeTrue();
    }

    [Theory]
    [InlineData(49, 0, 4.90)]     // just below the first tier
    [InlineData(50, 10, 4.50)]    // 5.00 - 10%
    [InlineData(99, 10, 8.91)]    // 9.90 - 0.99
    [InlineData(100, 15, 8.50)]   // 10.00 - 15%: the larger tier, not both
    [InlineData(250, 15, 21.25)]  // 25.00 - 3.75
    public void Volume_tiers_apply_at_their_thresholds_and_do_not_stack(
        int sides, int percent, double expected)
    {
        var price = PriceCalculator.Quote(Default(), Spec(sides));

        price.DiscountPercent.Should().Be(percent);
        price.FinalPrice.Should().Be((decimal)expected);
    }

    [Fact]
    public void Copies_count_towards_the_volume_tier()
    {
        // 10 pages x 5 copies = 50 sides: the first tier.
        var price = PriceCalculator.Quote(Default(), Spec(10, copies: 5));

        price.Sides.Should().Be(50);
        price.DiscountPercent.Should().Be(10m);
        price.FinalPrice.Should().Be(4.50m);
    }

    [Fact]
    public void Duplex_halves_the_sheets_but_not_the_sides()
    {
        // The price is per side; duplex only saves paper (plan section 2.3).
        var price = PriceCalculator.Quote(Default(), Spec(7, duplex: true));

        price.Sides.Should().Be(7);
        price.Sheets.Should().Be(4);
        price.FinalPrice.Should().Be(0.70m);
    }

    [Fact]
    public void Pages_per_sheet_rounds_the_last_side_up()
    {
        // 9 pages, 4 on a side: 3 sides, the last one only a quarter full.
        var price = PriceCalculator.Quote(Default(), Spec(9, perSheet: 4));

        price.Sides.Should().Be(3);
        price.FinalPrice.Should().Be(0.30m);
    }

    [Fact]
    public void Rounding_is_half_to_even()
    {
        // 1 side at 0.125 would be 0.12 half-to-even, 0.13 half-away.
        var table = Default() with { BwPerSide = 0.125m, MinOrder = 0m };

        PriceCalculator.Quote(table, Spec(1)).FinalPrice.Should().Be(0.12m);
        PriceCalculator.Quote(table, Spec(3)).FinalPrice.Should().Be(0.38m); // 0.375 -> 0.38
    }

    [Fact]
    public void A_paper_the_profile_does_not_price_is_refused()
    {
        var quote = () => PriceCalculator.Quote(Default(), Spec(1, paper: "Letter"));

        quote.Should().Throw<PricingException>().WithMessage("*Letter*");
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1000, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 17)]
    public void Nonsense_input_is_refused(int pages, int copies, int perSheet)
    {
        var quote = () => PriceCalculator.Quote(Default(), Spec(pages, copies: copies, perSheet: perSheet));

        quote.Should().Throw<PricingException>();
    }

    [Fact]
    public void The_breakdown_carries_the_rates_it_used()
    {
        // So an order can be explained and re-checked without the profile,
        // which may have changed by then.
        var price = PriceCalculator.Quote(Default(), Spec(4, color: true, paper: "A3"));

        price.PricePerSide.Should().Be(0.30m);
        price.PaperMultiplier.Should().Be(2m);
        price.Currency.Should().Be("AZN");
    }
}
