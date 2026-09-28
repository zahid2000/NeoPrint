using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The same request twice must act once (<c>docs/IDEMPOTENCY.md</c>).
/// </summary>
/// <remarks>
/// A caller that does not hear back cannot tell a lost response from a lost
/// request, so it retries. These cover what has to happen then, and what has to
/// happen when a key is reused for something else.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyTests
{
    private readonly NeoPrintApiFixture _fixture;

    public IdempotencyTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static object NewKiosk(string name) => new
    {
        name,
        lat = 40.3793,
        lng = 49.8486,
        address = "Bakı",
        defaultLanguage = "az",
        paperCapacityA4 = 500,
        paperCapacityA3 = 250,
        adminPin = "135790",
    };

    private async Task<HttpClient> SignedInSuperAdminAsync()
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

    private static HttpRequestMessage Post(string path, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyFilter.HeaderName, idempotencyKey);
        }

        return request;
    }



    // -----------------------------------------------------------------------
    // Replay
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_same_request_twice_creates_one_enrollment_token()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var key = $"enroll-{Guid.NewGuid():N}";
        var body = new { newKiosk = NewKiosk("Idempotent enrollment") };

        using var first = await admin.SendAsync(Post("/api/admin/enrollment-tokens", body, key));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("id").GetGuid();

        // The caller did not hear back and sent it again.
        using var second = await admin.SendAsync(Post("/api/admin/enrollment-tokens", body, key));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Same token, not a second one: the stored response is replayed.
        secondBody.GetProperty("id").GetGuid().Should().Be(firstId);
        secondBody.GetProperty("token").GetString()
            .Should().Be(firstBody.GetProperty("token").GetString());

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var stored = await context.EnrollmentTokens
            .CountDocumentsAsync(token => token.Id == firstId);

        stored.Should().Be(1);
    }

    [DockerFact]
    public async Task Without_a_key_each_request_acts_on_its_own()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var body = new { newKiosk = NewKiosk("No key at all") };

        using var first = await admin.SendAsync(Post("/api/admin/enrollment-tokens", body, idempotencyKey: null));
        using var second = await admin.SendAsync(Post("/api/admin/enrollment-tokens", body, idempotencyKey: null));

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        // No key, no protection: two tokens, which is what was asked for.
        secondBody.GetProperty("id").GetGuid()
            .Should().NotBe(firstBody.GetProperty("id").GetGuid());
    }

    // -----------------------------------------------------------------------
    // Conflict
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Reusing_a_key_for_a_different_request_is_a_conflict()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var key = $"reused-{Guid.NewGuid():N}";

        using var first = await admin.SendAsync(
            Post("/api/admin/enrollment-tokens", new { newKiosk = NewKiosk("First kiosk") }, key));

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await admin.SendAsync(
            Post("/api/admin/enrollment-tokens", new { newKiosk = NewKiosk("A different kiosk") }, key));

        // Answering with the first response would be answering a question that
        // was not asked (docs/IDEMPOTENCY.md).
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("Sorğu açarı təkrar istifadə olunub");
    }

    [DockerFact]
    public async Task One_callers_key_cannot_replay_another_callers_response()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var key = $"shared-{Guid.NewGuid():N}";
        var body = new { newKiosk = NewKiosk("Scoped to the caller") };

        using var mine = await admin.SendAsync(Post("/api/admin/enrollment-tokens", body, key));
        mine.StatusCode.Should().Be(HttpStatusCode.OK);

        // The same key, from nobody in particular: it must not open the door to
        // somebody else's response.
        using var anonymous = _fixture.CreateApiClient();
        using var theirs = await anonymous.SendAsync(Post("/api/admin/enrollment-tokens", body, key));

        theirs.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Failures do not lock the key
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_failed_request_leaves_the_key_free_to_retry()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var key = $"retry-{Guid.NewGuid():N}";

        // A kiosk that does not exist: the request is refused.
        var bad = new { kioskId = Guid.NewGuid() };

        using var failed = await admin.SendAsync(Post("/api/admin/enrollment-tokens", bad, key));
        failed.IsSuccessStatusCode.Should().BeFalse();

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var records = await context.IdempotencyKeys.CountDocumentsAsync(record => record.Key == key);

        // A failed request is one worth genuinely retrying, so the key must not
        // stay claimed by the attempt that failed.
        records.Should().Be(0);

        // And the same key now works.
        using var retried = await admin.SendAsync(
            Post("/api/admin/enrollment-tokens", new { newKiosk = NewKiosk("Good pin") }, key));

        retried.StatusCode.Should().Be(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task A_key_longer_than_the_limit_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        using var response = await admin.SendAsync(Post(
            "/api/admin/enrollment-tokens",
            new { newKiosk = NewKiosk("Long key") },
            new string('k', 200)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -----------------------------------------------------------------------
    // Provider events
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_provider_callback_is_acted_on_once()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IProviderEventStore>();

        var eventId = $"evt_{Guid.NewGuid():N}";

        var first = await events.TryRecordAsync("MockBank", eventId, "payment.succeeded", "hash-1", "corr-1");
        var second = await events.TryRecordAsync("MockBank", eventId, "payment.succeeded", "hash-1", "corr-2");

        // Providers resend until they get a 2xx, and sometimes afterwards.
        // Acting twice is a double refund.
        first.Should().BeTrue();
        second.Should().BeFalse();
    }

    [DockerFact]
    public async Task The_same_event_id_from_two_providers_is_two_events()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IProviderEventStore>();

        var eventId = $"evt_{Guid.NewGuid():N}";

        var bank = await events.TryRecordAsync("MockBank", eventId, "payment.succeeded", "hash", null);
        var fiscal = await events.TryRecordAsync("MockFiscal", eventId, "receipt.issued", "hash", null);

        // Providers number their events independently; the pair is what is
        // unique (docs/IDEMPOTENCY.md).
        bank.Should().BeTrue();
        fiscal.Should().BeTrue();
    }

    [DockerFact]
    public async Task Concurrent_deliveries_of_one_callback_let_exactly_one_through()
    {
        if (Skip) { return; }

        var eventId = $"evt_{Guid.NewGuid():N}";

        var scopes = Enumerable.Range(0, 8).Select(_ => _fixture.Services.CreateScope()).ToList();

        try
        {
            var attempts = scopes.Select(scope => scope.ServiceProvider
                .GetRequiredService<IProviderEventStore>()
                .TryRecordAsync("MockBank", eventId, "payment.succeeded", "hash", null));

            var results = await Task.WhenAll(attempts);

            // The unique index decides, not a check-then-act in the code.
            results.Count(accepted => accepted).Should().Be(1);
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }
    }
}
