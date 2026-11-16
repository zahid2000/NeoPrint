using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Price tiers that name a price, the graduated ladder and pricing campaigns
/// (T-436), through the admin API and the quote.
/// </summary>
/// <remarks>
/// Every campaign here is aimed at the test's own kiosk: one for every kiosk
/// would move the prices other tests in the collection expect.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PricingCampaignApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PricingCampaignApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<Guid> NewKioskAsync()
    {
        var context = _fixture.Services.GetRequiredService<MongoContext>();
        var kiosk = new Kiosk { Code = $"KSK-C{Random.Shared.Next(10000, 99999)}", Name = "Campaign test kiosk" };

        await context.Kiosks.InsertOneAsync(kiosk);

        return kiosk.Id;
    }

    /// <summary>The owner's example: up to 50 at 15 qəpik, from 51 at 13, from 101 at 11.</summary>
    private static object Ladder(string name, string tierMode) => new
    {
        name,
        bwPricePerSide = 0.15m,
        colorPricePerSide = 0.40m,
        minOrder = 0.20m,
        paperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = 2m },
        volumeTiers = new object[]
        {
            new { minSides = 51, discountPercent = 0m, bwPricePerSide = 0.13m, colorPricePerSide = 0.35m },
            new { minSides = 101, discountPercent = 0m, bwPricePerSide = 0.11m, colorPricePerSide = 0.30m },
        },
        tierMode,
    };

    private static async Task<JsonElement> QuoteAsync(HttpClient client, Guid kioskId, int pages)
    {
        var response = await client.PostAsJsonAsync("/api/pricing/quote", new
        {
            kioskId,
            options = new { pageCount = pages, copies = 1, duplex = false, color = false, paper = "A4", pagesPerSheet = 1 },
        });

        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.GetProperty("price");
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        return JsonDocument.Parse(raw).RootElement;
    }

    [DockerFact]
    public async Task A_profile_ladder_of_prices_is_read_as_the_profile_says()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        var created = await OkAsync(await admin.PostAsJsonAsync("/api/admin/pricing-profiles", Ladder($"Pilləli {Guid.NewGuid():N}", "WholeOrder")));
        var id = created.GetProperty("id").GetGuid();
        created.GetProperty("tierMode").GetString().Should().Be("WholeOrder");
        created.GetProperty("volumeTiers")[0].GetProperty("bwPricePerSide").GetDecimal().Should().Be(0.13m);

        await OkAsync(await admin.PostAsJsonAsync($"/api/admin/pricing-profiles/{id}/assign", new { kioskIds = new[] { kiosk } }));

        (await QuoteAsync(admin, kiosk, 50)).GetProperty("finalPrice").GetDecimal().Should().Be(7.50m);
        (await QuoteAsync(admin, kiosk, 120)).GetProperty("finalPrice").GetDecimal().Should().Be(13.20m, "all 120 at 0.11");

        await OkAsync(await admin.PutAsJsonAsync($"/api/admin/pricing-profiles/{id}", Ladder($"Pilləli {Guid.NewGuid():N}", "Graduated")));

        (await QuoteAsync(admin, kiosk, 120)).GetProperty("finalPrice").GetDecimal()
            .Should().Be(16.20m, "50 x 0.15 + 50 x 0.13 + 20 x 0.11");
    }

    [DockerFact]
    public async Task A_campaign_prices_its_kiosk_while_it_runs_and_not_after()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        // The seeded default: 10 x 0.10.
        (await QuoteAsync(admin, kiosk, 10)).GetProperty("finalPrice").GetDecimal().Should().Be(1.00m);

        var percent = await OkAsync(await admin.PostAsJsonAsync("/api/admin/pricing-campaigns", new
        {
            name = "Noyabr",
            kind = "Percent",
            percent = 20m,
            tierMode = "WholeOrder",
            kioskIds = new[] { kiosk },
        }));
        percent.GetProperty("state").GetString().Should().Be("Running");

        var price = await QuoteAsync(admin, kiosk, 10);
        price.GetProperty("finalPrice").GetDecimal().Should().Be(0.80m);
        price.GetProperty("campaignName").GetString().Should().Be("Noyabr");
        price.GetProperty("campaignDiscount").GetDecimal().Should().Be(0.20m);

        // A price campaign that started later at the same kiosk takes over.
        var prices = await OkAsync(await admin.PostAsJsonAsync("/api/admin/pricing-campaigns", new
        {
            name = "Tələbə",
            kind = "Price",
            bwPricePerSide = 0.05m,
            colorPricePerSide = 0.20m,
            volumeTiers = Array.Empty<object>(),
            tierMode = "WholeOrder",
            startsAt = DateTime.UtcNow.AddMinutes(-1),
            kioskIds = new[] { kiosk },
        }));

        (await QuoteAsync(admin, kiosk, 10)).GetProperty("finalPrice").GetDecimal().Should().Be(0.50m);

        // Ended: back to the percentage one.
        var pricesId = prices.GetProperty("id").GetGuid();
        var ended = await OkAsync(await admin.PutAsJsonAsync($"/api/admin/pricing-campaigns/{pricesId}", new
        {
            name = "Tələbə",
            kind = "Price",
            bwPricePerSide = 0.05m,
            colorPricePerSide = 0.20m,
            tierMode = "WholeOrder",
            startsAt = DateTime.UtcNow.AddDays(-2),
            endsAt = DateTime.UtcNow.AddDays(-1),
            kioskIds = new[] { kiosk },
        }));
        ended.GetProperty("state").GetString().Should().Be("Ended");
        (await QuoteAsync(admin, kiosk, 10)).GetProperty("finalPrice").GetDecimal().Should().Be(0.80m);

        // Switched off, then deleted: the profile's price again.
        var percentId = percent.GetProperty("id").GetGuid();
        var off = await OkAsync(await admin.PutAsJsonAsync($"/api/admin/pricing-campaigns/{percentId}", new
        {
            name = "Noyabr",
            kind = "Percent",
            percent = 20m,
            tierMode = "WholeOrder",
            kioskIds = new[] { kiosk },
            isActive = false,
        }));
        off.GetProperty("state").GetString().Should().Be("Off");
        (await QuoteAsync(admin, kiosk, 10)).GetProperty("finalPrice").GetDecimal().Should().Be(1.00m);

        (await admin.DeleteAsync($"/api/admin/pricing-campaigns/{percentId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/admin/pricing-campaigns/{percentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await OkAsync(await admin.GetAsync("/api/admin/pricing-campaigns"));
        list.EnumerateArray().Should().Contain(c => c.GetProperty("id").GetGuid() == pricesId);

        // Every change is in the audit log.
        var context = _fixture.Services.GetRequiredService<MongoContext>();
        (await context.AuditLogs.Find(a => a.EntityId == percentId.ToString()).ToListAsync())
            .Select(a => a.Action).Should().Contain(["pricing.campaign.create", "pricing.campaign.update", "pricing.campaign.delete"]);
    }

    [DockerFact]
    public async Task A_campaign_that_could_not_price_anything_is_refused()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        var noPercent = await admin.PostAsJsonAsync("/api/admin/pricing-campaigns", new
        {
            name = "Boş",
            kind = "Percent",
            percent = 0m,
            tierMode = "WholeOrder",
            kioskIds = new[] { kiosk },
        });
        noPercent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var backwards = await admin.PostAsJsonAsync("/api/admin/pricing-campaigns", new
        {
            name = "Tərs",
            kind = "Percent",
            percent = 10m,
            tierMode = "WholeOrder",
            startsAt = DateTime.UtcNow,
            endsAt = DateTime.UtcNow.AddDays(-1),
            kioskIds = new[] { kiosk },
        });
        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unknownKiosk = await admin.PostAsJsonAsync("/api/admin/pricing-campaigns", new
        {
            name = "Heç yer",
            kind = "Percent",
            percent = 10m,
            tierMode = "WholeOrder",
            kioskIds = new[] { Guid.NewGuid() },
        });
        unknownKiosk.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.GetAsync("/api/admin/pricing-campaigns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
