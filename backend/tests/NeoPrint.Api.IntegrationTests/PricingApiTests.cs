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
/// Quotes and pricing profiles (plan sections 2.3, 6, 8A.3), including step
/// 2's definition of done: "Kioskun qiymət profili dəyişəndə quote nəticəsi
/// dəyişir, həcm endirimi pillələri düzgün tətbiq olunur".
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PricingApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PricingApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

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

    /// <summary>A kiosk of this test's own, so nothing else's prices move.</summary>
    private async Task<Guid> NewKioskAsync()
    {
        var context = _fixture.Services.GetRequiredService<MongoContext>();
        var kiosk = new Kiosk { Code = $"KSK-T{Random.Shared.Next(10000, 99999)}", Name = "Pricing test kiosk" };

        await context.Kiosks.InsertOneAsync(kiosk);

        return kiosk.Id;
    }

    private static object Profile(string name, decimal bw = 0.15m, decimal colour = 0.40m, decimal a3 = 2m) => new
    {
        name,
        bwPricePerSide = bw,
        colorPricePerSide = colour,
        minOrder = 0.30m,
        paperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = a3 },
        volumeTiers = new[]
        {
            new { minSides = 20, discountPercent = 5m },
            new { minSides = 40, discountPercent = 20m },
        },
    };

    private static async Task<decimal> QuoteAsync(HttpClient client, Guid? kioskId, int pages, bool colour = false)
    {
        var response = await client.PostAsJsonAsync("/api/pricing/quote", new
        {
            kioskId,
            options = new { pageCount = pages, copies = 1, duplex = false, color = colour, paper = "A4", pagesPerSheet = 1 },
        });

        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.GetProperty("price").GetProperty("finalPrice").GetDecimal();
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, object profile)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/pricing-profiles", profile);
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetGuid();
    }

    [DockerFact]
    public async Task A_kiosk_without_a_profile_is_priced_by_the_default()
    {
        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        // The seeded default: 10 x 0.10.
        (await QuoteAsync(admin, kiosk, 10)).Should().Be(1.00m);
        (await QuoteAsync(admin, kioskId: null, 10)).Should().Be(1.00m);
    }

    [DockerFact]
    public async Task Changing_a_kiosks_profile_changes_its_quote()
    {
        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        var before = await QuoteAsync(admin, kiosk, 10);

        var profile = await CreateAsync(admin, Profile($"Mall {Guid.NewGuid():N}"));
        var assign = await admin.PostAsJsonAsync($"/api/admin/pricing-profiles/{profile}/assign", new { kioskIds = new[] { kiosk } });
        assign.StatusCode.Should().Be(HttpStatusCode.OK, await assign.Content.ReadAsStringAsync());

        var after = await QuoteAsync(admin, kiosk, 10);

        before.Should().Be(1.00m);
        after.Should().Be(1.50m, "10 sides at the new profile's 0.15");

        // And the profile's own edit reaches the kiosk's next quote.
        var edit = await admin.PutAsJsonAsync($"/api/admin/pricing-profiles/{profile}", Profile("renamed " + Guid.NewGuid().ToString("N"), bw: 0.25m));
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

        (await QuoteAsync(admin, kiosk, 10)).Should().Be(2.50m);
    }

    [DockerFact]
    public async Task Volume_tiers_apply_at_their_thresholds()
    {
        using var admin = await SuperAdminAsync();
        var kiosk = await NewKioskAsync();

        var profile = await CreateAsync(admin, Profile($"Tiers {Guid.NewGuid():N}"));
        await admin.PostAsJsonAsync($"/api/admin/pricing-profiles/{profile}/assign", new { kioskIds = new[] { kiosk } });

        // 0.15 a side; tiers at 20 (5%) and 40 (20%).
        (await QuoteAsync(admin, kiosk, 19)).Should().Be(2.85m);   // no tier
        (await QuoteAsync(admin, kiosk, 20)).Should().Be(2.85m);   // 3.00 - 5%
        (await QuoteAsync(admin, kiosk, 39)).Should().Be(5.56m);   // 5.85 - 0.29 (0.2925 -> 0.29)
        (await QuoteAsync(admin, kiosk, 40)).Should().Be(4.80m);   // 6.00 - 20%, the larger tier only
    }

    [DockerFact]
    public async Task The_default_profile_cannot_be_deleted_nor_one_that_kiosks_use()
    {
        using var admin = await SuperAdminAsync();

        var profiles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/pricing-profiles");
        var defaultId = profiles.EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetGuid();

        (await admin.DeleteAsync($"/api/admin/pricing-profiles/{defaultId}")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var kiosk = await NewKioskAsync();
        var used = await CreateAsync(admin, Profile($"Used {Guid.NewGuid():N}"));
        await admin.PostAsJsonAsync($"/api/admin/pricing-profiles/{used}/assign", new { kioskIds = new[] { kiosk } });

        var refused = await admin.DeleteAsync($"/api/admin/pricing-profiles/{used}");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("1 kioska");

        var unused = await CreateAsync(admin, Profile($"Unused {Guid.NewGuid():N}"));
        (await admin.DeleteAsync($"/api/admin/pricing-profiles/{unused}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task A_profile_that_cannot_price_an_order_is_refused()
    {
        using var admin = await SuperAdminAsync();

        var noA4 = new
        {
            name = $"No A4 {Guid.NewGuid():N}",
            bwPricePerSide = 0.1m,
            colorPricePerSide = 0.3m,
            minOrder = 0.2m,
            paperMultipliers = new Dictionary<string, decimal> { ["A3"] = 2m },
            volumeTiers = Array.Empty<object>(),
        };

        var response = await admin.PostAsJsonAsync("/api/admin/pricing-profiles", noA4);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("A4");

        var freeForAll = new
        {
            name = $"Hundred {Guid.NewGuid():N}",
            bwPricePerSide = 0.1m,
            colorPricePerSide = 0.3m,
            minOrder = 0.2m,
            paperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m },
            volumeTiers = new[] { new { minSides = 10, discountPercent = 100m } },
        };

        (await admin.PostAsJsonAsync("/api/admin/pricing-profiles", freeForAll)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a 100% tier would print for free");
    }

    [DockerFact]
    public async Task An_edit_is_kept_in_the_profiles_history()
    {
        using var admin = await SuperAdminAsync();

        var name = $"History {Guid.NewGuid():N}";
        var id = await CreateAsync(admin, Profile(name));

        var edited = await admin.PutAsJsonAsync($"/api/admin/pricing-profiles/{id}", Profile(name, bw: 0.20m));
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("bwPricePerSide").GetDecimal().Should().Be(0.20m);
        body.GetProperty("revision").GetInt32().Should().BeGreaterThan(0);

        var context = _fixture.Services.GetRequiredService<MongoContext>();
        var stored = await context.PricingProfiles.Find(p => p.Id == id).FirstAsync();

        stored.History.Should().ContainSingle();
        stored.History[0].Before.Should().Contain("0.15");
    }

    [DockerFact]
    public async Task The_calculator_prices_values_that_are_not_saved_yet()
    {
        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/pricing-profiles/calculate", new
        {
            draft = Profile("draft", colour: 0.50m, a3: 3m),
            options = new { pageCount = 4, copies = 1, duplex = false, color = true, paper = "A3", pagesPerSheet = 1 },
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("finalPrice").GetDecimal().Should().Be(6.00m, "4 x 0.50 x 3");
    }

    [DockerFact]
    public async Task Making_a_profile_the_default_leaves_exactly_one()
    {
        using var admin = await SuperAdminAsync();

        var profiles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/pricing-profiles");
        var originalDefault = profiles.EnumerateArray().Single(p => p.GetProperty("isDefault").GetBoolean())
            .GetProperty("id").GetGuid();

        var candidate = await CreateAsync(admin, Profile($"Next default {Guid.NewGuid():N}"));

        try
        {
            (await admin.PostAsync($"/api/admin/pricing-profiles/{candidate}/default", content: null)).StatusCode
                .Should().Be(HttpStatusCode.OK);

            var after = await admin.GetFromJsonAsync<JsonElement>("/api/admin/pricing-profiles");
            after.EnumerateArray().Where(p => p.GetProperty("isDefault").GetBoolean())
                .Select(p => p.GetProperty("id").GetGuid())
                .Should().Equal(candidate);
        }
        finally
        {
            // Put the seeded default back, so the other tests' "default" quotes hold.
            await admin.PostAsync($"/api/admin/pricing-profiles/{originalDefault}/default", content: null);
        }
    }

    [DockerFact]
    public async Task A_quote_needs_a_signed_in_caller_and_sensible_options()
    {
        using var anonymous = _fixture.CreateApiClient();

        (await anonymous.PostAsJsonAsync("/api/pricing/quote", new { options = new { pageCount = 1 } })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using var admin = await SuperAdminAsync();

        var zero = await admin.PostAsJsonAsync("/api/pricing/quote", new { options = new { pageCount = 0 } });
        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var letter = await admin.PostAsJsonAsync("/api/pricing/quote", new { options = new { pageCount = 1, paper = "Letter" } });
        letter.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await letter.Content.ReadAsStringAsync()).Should().Contain("Letter");
    }
}
