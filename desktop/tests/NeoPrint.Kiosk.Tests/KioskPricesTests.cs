using System.Text.Json;
using System.Text.Json.Serialization;

using NeoPrint.Kiosk.Core;
using NeoPrint.Pricing;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's live price uses the backend's price tiers, ladder and running
/// campaign (T-436) as the configuration carries them - and a configuration
/// from before them still prices as it did.
/// </summary>
public sealed class KioskPricesTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static PrintSpec Pages(int pages) => new(pages, 1, 1, false, false, "A4");

    [Fact]
    public void Price_tiers_the_graduated_ladder_and_a_campaign_reach_the_kiosks_quote()
    {
        const string json = """
            {
              "profileName": "Standart",
              "bwPerSide": 0.15, "colorPerSide": 0.40,
              "paperMultipliers": { "A4": 1, "A3": 2 },
              "minOrder": 0.20,
              "tiers": [
                { "minSides": 51, "discountPercent": 0, "bwPerSide": 0.13, "colorPerSide": 0.35 },
                { "minSides": 101, "discountPercent": 0, "bwPerSide": 0.11, "colorPerSide": 0.30 }
              ],
              "currency": "AZN",
              "tierMode": "Graduated",
              "campaignPercent": 10,
              "campaignName": "Noyabr"
            }
            """;

        var prices = JsonSerializer.Deserialize<KioskPrices>(json, Web)!;
        var price = PriceCalculator.Quote(prices.ToPriceTable(), Pages(120));

        // 50 x 0.15 + 50 x 0.13 + 20 x 0.11 = 16.20, less 10% = 14.58.
        price.FinalPrice.Should().Be(14.58m);
        price.CampaignName.Should().Be("Noyabr");
    }

    [Fact]
    public void A_configuration_from_before_price_tiers_prices_as_it_did()
    {
        const string json = """
            {
              "profileName": "Standart",
              "bwPerSide": 0.10, "colorPerSide": 0.30,
              "paperMultipliers": { "A4": 1 },
              "minOrder": 0.20,
              "tiers": [ { "minSides": 50, "discountPercent": 10 }, { "minSides": 100, "discountPercent": 15 } ],
              "currency": "AZN"
            }
            """;

        var prices = JsonSerializer.Deserialize<KioskPrices>(json, Web)!;

        prices.TierMode.Should().Be(TierMode.WholeOrder);
        PriceCalculator.Quote(prices.ToPriceTable(), Pages(100)).FinalPrice.Should().Be(8.50m);
    }
}
