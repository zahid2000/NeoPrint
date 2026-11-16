using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Pricing;

namespace NeoPrint.Domain.Tests.Pricing;

/// <summary>
/// Pricing campaigns and price tiers (T-436): which campaign prices a kiosk,
/// what it does to the profile's rates, and what an administrator may enter.
/// </summary>
public sealed class PricingCampaignTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Kiosk = Guid.NewGuid();

    private static PricingProfile Profile() => new()
    {
        Name = "Standart",
        BwPricePerSide = Money.FromAzn(0.15m),
        ColorPricePerSide = Money.FromAzn(0.40m),
        MinOrder = Money.FromAzn(0.20m),
        VolumeTiers =
        [
            new VolumeTier { MinSides = 51, BwPricePerSide = 0.13m, ColorPricePerSide = 0.35m },
            new VolumeTier { MinSides = 101, BwPricePerSide = 0.11m, ColorPricePerSide = 0.30m },
        ],
    };

    private static PricingCampaign Percent(decimal percent, DateTime? from = null, DateTime? until = null, params Guid[] kiosks) => new()
    {
        Name = $"{percent}%",
        Kind = CampaignKind.Percent,
        Percent = percent,
        StartsAt = from,
        EndsAt = until,
        KioskIds = [.. kiosks],
    };

    private static PrintSpec Pages(int pages) => new(pages, 1, 1, false, false, "A4");

    [Fact]
    public void A_profile_tier_can_name_its_price_and_the_profile_can_read_the_ladder_band_by_band()
    {
        var profile = Profile();

        PriceCalculator.Quote(profile.ToPriceTable(), Pages(120)).FinalPrice.Should().Be(13.20m, "all 120 at 0.11");

        profile.TierMode = TierMode.Graduated;
        PriceCalculator.Quote(profile.ToPriceTable(), Pages(120)).FinalPrice.Should().Be(16.20m, "50 x 0.15 + 50 x 0.13 + 20 x 0.11");
        profile.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_price_tier_needs_both_prices_and_a_percentage_tier_a_sensible_percentage()
    {
        var profile = Profile();
        profile.VolumeTiers.Add(new VolumeTier { MinSides = 200, BwPricePerSide = 0.10m });
        profile.VolumeTiers.Add(new VolumeTier { MinSides = 300, DiscountPercent = 100m });

        profile.Validate().Select(p => p.Message).Should().Contain(["pricing.tierPricesRequired", "pricing.tierDiscountRange"]);
    }

    [Fact]
    public void A_campaign_runs_inside_its_dates_while_it_is_switched_on()
    {
        Percent(10).IsRunning(Now).Should().BeTrue("no dates: from now until switched off");
        Percent(10, from: Now.AddDays(1)).IsRunning(Now).Should().BeFalse("not yet");
        Percent(10, until: Now).IsRunning(Now).Should().BeFalse("the end is not included");
        Percent(10, Now.AddDays(-1), Now.AddDays(1)).IsRunning(Now).Should().BeTrue();

        var off = Percent(10);
        off.IsActive = false;
        off.IsRunning(Now).Should().BeFalse();
    }

    [Fact]
    public void One_aimed_at_the_kiosk_beats_one_for_every_kiosk_then_the_latest_start()
    {
        var all = Percent(10, from: Now.AddDays(-1));
        var here = Percent(20, from: Now.AddDays(-5), kiosks: Kiosk);
        var elsewhere = Percent(50, kiosks: Guid.NewGuid());

        PricingCampaign.Pick([all, here, elsewhere], Kiosk, Now).Should().BeSameAs(here);
        PricingCampaign.Pick([all, elsewhere], Kiosk, Now).Should().BeSameAs(all);

        var later = Percent(30, from: Now.AddHours(-1), kiosks: Kiosk);
        PricingCampaign.Pick([all, here, later], Kiosk, Now).Should().BeSameAs(later);

        PricingCampaign.Pick([elsewhere], Kiosk, Now).Should().BeNull();
    }

    [Fact]
    public void A_percentage_campaign_comes_on_top_of_the_profile_and_its_ladder()
    {
        var rates = Percent(20).Apply(Profile().ToPriceTable());

        var price = PriceCalculator.Quote(rates, Pages(120));

        price.FinalPrice.Should().Be(10.56m, "13.20 less 20%");
        price.CampaignName.Should().Be("20%");
        price.CampaignDiscount.Should().Be(2.64m);
    }

    [Fact]
    public void A_price_campaign_replaces_the_profiles_prices_and_ladder_but_keeps_its_minimum_and_paper()
    {
        var campaign = new PricingCampaign
        {
            Name = "Tələbə",
            Kind = CampaignKind.Price,
            BwPricePerSide = 0.08m,
            ColorPricePerSide = 0.25m,
            TierMode = TierMode.Graduated,
            VolumeTiers = [new VolumeTier { MinSides = 11, BwPricePerSide = 0.05m, ColorPricePerSide = 0.20m }],
        };
        campaign.Validate().Should().BeEmpty();

        var rates = campaign.Apply(Profile().ToPriceTable());

        // 10 x 0.08 + 10 x 0.05 = 1.30
        PriceCalculator.Quote(rates, Pages(20)).FinalPrice.Should().Be(1.30m);
        // One side: 0.08 under the profile's 0.20 minimum.
        PriceCalculator.Quote(rates, Pages(1)).FinalPrice.Should().Be(0.20m);
        rates.PaperMultipliers.Should().ContainKey("A3");
    }

    [Fact]
    public void A_campaign_says_what_is_wrong_with_it()
    {
        var problems = new PricingCampaign
        {
            Name = " ",
            Kind = CampaignKind.Percent,
            Percent = 0,
            StartsAt = Now,
            EndsAt = Now,
        }.Validate().Select(p => p.Message);

        problems.Should().Contain(["pricing.campaignNameRequired", "pricing.campaignPercentRange", "pricing.campaignDatesOrder"]);

        new PricingCampaign { Name = "Qiymət", Kind = CampaignKind.Price, BwPricePerSide = 0.10m }
            .Validate().Select(p => p.Message).Should().Contain("pricing.campaignPricesRequired");
    }

    [Fact]
    public void The_snapshot_keeps_the_campaign_that_priced_the_order()
    {
        var campaign = Percent(25);

        var snapshot = PriceSnapshot.Take(Profile(), campaign, Pages(10), Now);

        snapshot.CampaignId.Should().Be(campaign.Id);
        snapshot.CampaignName.Should().Be("25%");
        snapshot.Price.FinalPrice.Should().Be(1.12m, "1.50 less 0.38 - the 0.375 discount rounded half to even");
        snapshot.Recompute().Should().Be(snapshot.Price);
    }
}
