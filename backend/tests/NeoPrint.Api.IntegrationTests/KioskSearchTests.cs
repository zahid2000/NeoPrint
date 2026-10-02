using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The app's kiosks tab (plan step 5, sections 6 and 8.2): text and
/// <c>2dsphere</c> search, the kiosk detail with prices, queue and paper.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskSearchTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskSearchTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task Text_finds_a_kiosk_by_name_code_address_or_id_in_any_case()
    {
        if (Skip)
        {
            return;
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        using var kiosk = await ReadyKioskAsync(_fixture, "search-text");
        await PlaceKioskAsync(kiosk.KioskId, $"Axtarış Mərkəzi {tag}", $"Nizami küçəsi {tag}", 40.40, 49.85);
        using var user = (await TestUsers.ConfirmedAsync(_fixture)).Client;

        (await IdsAsync(user, $"?search=mərkəzi {tag}")).Should().ContainSingle().Which.Should().Be(kiosk.KioskId);
        (await IdsAsync(user, $"?search={Uri.EscapeDataString($"küçəsi {tag.ToUpperInvariant()}")}")).Should().ContainSingle().Which.Should().Be(kiosk.KioskId);
        (await IdsAsync(user, $"?search={kiosk.KioskCode.ToLowerInvariant()}")).Should().ContainSingle().Which.Should().Be(kiosk.KioskId);
        (await IdsAsync(user, $"?search={kiosk.KioskId}")).Should().Equal(kiosk.KioskId);

        // The text is matched as text: a pattern does not match everything.
        (await IdsAsync(user, "?search=.*")).Should().BeEmpty();
        (await user.GetAsync("/api/kiosks?search=(")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Near_a_point_the_nearest_comes_first_with_its_distance_and_the_radius_is_kept()
    {
        if (Skip)
        {
            return;
        }

        // A spot of open sea no other test puts a kiosk in.
        var lat = -30 - Random.Shared.NextDouble();
        var lng = -20 - Random.Shared.NextDouble();

        using var near = await ReadyKioskAsync(_fixture, "search-near");
        using var far = await ReadyKioskAsync(_fixture, "search-far");
        await PlaceKioskAsync(near.KioskId, "Yaxın kiosk", "Yaxın", lat + 0.01, lng);
        await PlaceKioskAsync(far.KioskId, "Uzaq kiosk", "Uzaq", lat + 0.05, lng);
        using var user = (await TestUsers.ConfirmedAsync(_fixture)).Client;

        var point = FormattableString.Invariant($"lat={lat}&lng={lng}");

        var within3 = await ItemsAsync(user, $"?{point}&radiusKm=3");
        within3.Select(Id).Should().Equal(near.KioskId);
        within3[0].GetProperty("distanceKm").GetDouble().Should().BeApproximately(1.11, 0.02);

        var within10 = await ItemsAsync(user, $"?{point}&radiusKm=10");
        within10.Select(Id).Should().Equal(near.KioskId, far.KioskId);
        within10[1].GetProperty("distanceKm").GetDouble().Should().BeApproximately(5.56, 0.05);

        // Text and place together: only what matches both, still by distance.
        (await IdsAsync(user, $"?{point}&radiusKm=10&search=uzaq")).Should().Equal(far.KioskId);

        // Without a location there is no distance.
        (await ItemsAsync(user, $"?search={near.KioskCode}")).Single().TryGetProperty("distanceKm", out _).Should().BeFalse();
    }

    [DockerFact]
    public async Task The_detail_shows_prices_capabilities_the_queue_and_how_much_paper_is_left()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "search-detail");
        using var user = (await TestUsers.ConfirmedAsync(_fixture)).Client;

        var detail = await DetailAsync(user, kiosk.KioskCode.ToLowerInvariant());
        detail.GetProperty("id").GetGuid().Should().Be(kiosk.KioskId);
        detail.GetProperty("status").GetString().Should().Be("Online");
        detail.GetProperty("capabilities").GetProperty("color").GetBoolean().Should().BeTrue();
        detail.GetProperty("prices").GetProperty("bwPerSide").GetDecimal().Should().BeGreaterThan(0);
        detail.GetProperty("queueLength").GetInt32().Should().Be(0);
        Paper(detail, "A4").Should().Be((500, "Ok"));
        detail.GetProperty("paper").EnumerateArray().Should().NotContain(p => p.GetProperty("size").GetString() == "A3", "the printer has no A3");

        // A queued job is in the queue and its sheets are no longer free.
        await PaidBankJobAsync(_fixture, kiosk, pages: 3);
        detail = await DetailAsync(user, kiosk.KioskId.ToString());
        detail.GetProperty("queueLength").GetInt32().Should().Be(1);
        Paper(detail, "A4").Should().Be((497, "Ok"));
        (await user.GetFromJsonAsync<JsonElement>($"/api/kiosks/{kiosk.KioskId}/queue")).GetProperty("length").GetInt32().Should().Be(1);

        await SetAsync(kiosk.KioskId, Builders<Kiosk>.Update.Set("PaperStock.A4.Current", 150));
        Paper(await DetailAsync(user, kiosk.KioskCode), "A4").Should().Be((147, "Low"));

        await SetAsync(kiosk.KioskId, Builders<Kiosk>.Update.Set("PaperStock.A4.Current", 2));
        Paper(await DetailAsync(user, kiosk.KioskCode), "A4").Should().Be((0, "Out"));
    }

    [DockerFact]
    public async Task A_kiosk_a_customer_cannot_go_to_is_not_found_and_a_silent_one_is_offline()
    {
        if (Skip)
        {
            return;
        }

        using var user = (await TestUsers.ConfirmedAsync(_fixture)).Client;
        using var kiosk = await ReadyKioskAsync(_fixture, "search-hidden");

        foreach (var status in new[] { KioskStatus.NotActivated, KioskStatus.NotConfigured, KioskStatus.Disabled })
        {
            await SetAsync(kiosk.KioskId, Builders<Kiosk>.Update.Set(k => k.Status, status));
            (await user.GetAsync($"/api/kiosks/{kiosk.KioskCode}")).StatusCode.Should().Be(HttpStatusCode.NotFound, status.ToString());
            (await user.GetAsync($"/api/kiosks/{kiosk.KioskId}/queue")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await IdsAsync(user, $"?search={kiosk.KioskCode}")).Should().BeEmpty();
        }

        // Stored Online, but it stopped calling in: the customer is told Offline.
        await SetAsync(
            kiosk.KioskId,
            Builders<Kiosk>.Update.Set(k => k.Status, KioskStatus.Online).Set(k => k.LastHeartbeatAt, DateTime.UtcNow.AddHours(-2)));
        (await DetailAsync(user, kiosk.KioskCode)).GetProperty("status").GetString().Should().Be("Offline");
        (await ItemsAsync(user, $"?search={kiosk.KioskCode}")).Single().GetProperty("status").GetString().Should().Be("Offline");

        (await user.GetAsync("/api/kiosks/KSK-NOPE")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Only_a_signed_in_app_user_searches_and_bad_input_is_refused()
    {
        if (Skip)
        {
            return;
        }

        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.GetAsync("/api/kiosks")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var kiosk = await ReadyKioskAsync(_fixture, "search-auth");
        (await kiosk.Client.GetAsync("/api/kiosks")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var user = (await TestUsers.ConfirmedAsync(_fixture)).Client;

        foreach (var (query, field) in new[]
                 {
                     ("?lat=40.4", "lat"),
                     ("?radiusKm=5", "radiusKm"),
                     ("?lat=40.4&lng=49.8&radiusKm=500", "radiusKm"),
                     ("?lat=95&lng=49.8", "lat"),
                     ($"?search={new string('a', 101)}", "search"),
                 })
        {
            var response = await user.GetAsync($"/api/kiosks{query}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
            body.Should().Contain($"\"{field}\"", query);
        }
    }

    [DockerFact]
    public async Task A_kiosk_saved_before_the_search_gets_its_point_when_the_indexes_are_made()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "search-legacy");

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var raw = context.Database.GetCollection<BsonDocument>("kiosks");
        var byId = Builders<BsonDocument>.Filter.Eq("_id", new BsonBinaryData(kiosk.KioskId, GuidRepresentation.Standard));

        (await raw.Find(byId).SingleAsync())["Location"]["Point"]["coordinates"].AsBsonArray
            .Select(value => value.AsDouble).Should().Equal(49.8486, 40.3793);

        await raw.UpdateOneAsync(byId, Builders<BsonDocument>.Update.Unset("Location.Point"));
        await context.EnsureIndexesAsync();

        var point = (await raw.Find(byId).SingleAsync())["Location"]["Point"].AsBsonDocument;
        point["type"].AsString.Should().Be("Point");
        point["coordinates"].AsBsonArray.Select(value => value.AsDouble).Should().Equal(49.8486, 40.3793);
    }

    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();

    private static (int? Sheets, string State) Paper(JsonElement detail, string size)
    {
        var paper = detail.GetProperty("paper").EnumerateArray().Single(p => p.GetProperty("size").GetString() == size);
        return (paper.TryGetProperty("availableSheets", out var sheets) ? sheets.GetInt32() : null, paper.GetProperty("state").GetString()!);
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient user, string query)
    {
        var response = await user.GetAsync($"/api/kiosks{query}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()];
    }

    private static async Task<List<Guid>> IdsAsync(HttpClient user, string query)
        => [.. (await ItemsAsync(user, query)).Select(Id)];

    private static async Task<JsonElement> DetailAsync(HttpClient user, string idOrCode)
    {
        var response = await user.GetAsync($"/api/kiosks/{idOrCode}");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Through the admin edit form, so the location is saved the way the panel saves it.</summary>
    private async Task PlaceKioskAsync(Guid kioskId, string name, string address, double lat, double lng)
    {
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/kiosks/{kioskId}")
        {
            Content = JsonContent.Create(new { name, lat, lng, address, defaultLanguage = "az", queueDisplayLimit = (int?)null }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await admin.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task SetAsync(Guid kioskId, UpdateDefinition<Kiosk> update)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(k => k.Id, kioskId), update);
    }
}
