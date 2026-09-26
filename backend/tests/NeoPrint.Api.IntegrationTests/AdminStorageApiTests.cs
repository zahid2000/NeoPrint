using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The storage page's API (plan sections 2.10, 8A.3).
/// </summary>
/// <remarks>
/// Runs against the application's own store, so nothing here starts a sync:
/// a background copy onto the store every other test uses would race them.
/// The sync itself is proven in <see cref="StorageMigrationTests"/> and
/// <see cref="StorageMirrorTests"/>; this covers the rules around it.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdminStorageApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminStorageApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

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

    private object Input(string name, string? secret = NeoPrintApiFixture.StorageSecretKey) => new
    {
        name,
        type = "RustFS",
        endpoint = _fixture.StorageEndpoint,
        region = "us-east-1",
        useSsl = false,
        accessKey = NeoPrintApiFixture.StorageAccessKey,
        secretKey = secret,
        buckets = new
        {
            files = NeoPrintApiFixture.FilesBucket,
            receipts = NeoPrintApiFixture.ReceiptsBucket,
            temp = NeoPrintApiFixture.TempBucket,
        },
    };

    [DockerFact]
    public async Task The_seeded_provider_is_listed_without_its_secret()
    {
        using var admin = await SuperAdminAsync();

        var response = await admin.GetAsync("/api/admin/storage/providers");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        // Not the secret, and not the access key either - only its tail.
        raw.Should().NotContain(NeoPrintApiFixture.StorageSecretKey);
        raw.Should().NotContain(NeoPrintApiFixture.StorageAccessKey);
        raw.Should().NotContain("Ciphertext");

        var providers = JsonDocument.Parse(raw).RootElement;
        var primary = providers.EnumerateArray().Single(p => p.GetProperty("role").GetString() == "Primary");

        primary.GetProperty("accessKeyHint").GetString().Should().Be("••••test");
        primary.GetProperty("isVerified").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Credentials_are_tested_before_anything_is_saved()
    {
        using var admin = await SuperAdminAsync();

        var good = await admin.PostAsJsonAsync("/api/admin/storage/providers/test", Input("Sınaq"));
        var goodBody = await good.Content.ReadFromJsonAsync<JsonElement>();

        good.StatusCode.Should().Be(HttpStatusCode.OK);
        goodBody.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        goodBody.GetProperty("buckets").GetArrayLength().Should().Be(3);

        var bad = await admin.PostAsJsonAsync("/api/admin/storage/providers/test", Input("Sınaq", "wrong-secret"));
        var badBody = await bad.Content.ReadFromJsonAsync<JsonElement>();

        badBody.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        badBody.GetProperty("message").GetString().Should().Contain("Secret key");
    }

    [DockerFact]
    public async Task A_provider_whose_test_fails_is_not_saved()
    {
        using var admin = await SuperAdminAsync();
        var name = $"Broken {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/admin/storage/providers", Input(name, "wrong-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Bağlantı testi keçmədi");

        var providers = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/providers");
        providers.EnumerateArray().Should().NotContain(p => p.GetProperty("name").GetString() == name);
    }

    [DockerFact]
    public async Task A_provider_that_passes_is_saved_inactive_and_verified()
    {
        using var admin = await SuperAdminAsync();
        var name = $"Second store {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/admin/storage/providers", Input(name));
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        raw.Should().NotContain(NeoPrintApiFixture.StorageSecretKey);

        var added = JsonDocument.Parse(raw).RootElement;

        // Saved, but it takes no writes until somebody gives it a role.
        added.GetProperty("role").GetString().Should().Be("Inactive");
        added.GetProperty("isVerified").GetBoolean().Should().BeTrue();

        // The name is the handle in the panel, so it has to be unique.
        var again = await admin.PostAsJsonAsync("/api/admin/storage/providers", Input(name));
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task An_edit_that_leaves_the_secret_empty_keeps_the_stored_one()
    {
        using var admin = await SuperAdminAsync();
        var name = $"Editable {Guid.NewGuid():N}";

        var added = await (await admin.PostAsJsonAsync("/api/admin/storage/providers", Input(name)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("id").GetString();

        // The form never receives the secret, so it cannot send it back: an
        // empty one has to mean "unchanged", or every edit would lock the
        // provider out.
        var renamed = await admin.PutAsJsonAsync($"/api/admin/storage/providers/{id}", Input(name + " (renamed)", secret: null));
        var body = await renamed.Content.ReadAsStringAsync();

        renamed.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var test = await admin.PostAsync($"/api/admin/storage/providers/{id}/test", content: null);
        (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("succeeded").GetBoolean()
            .Should().BeTrue("the stored secret was kept");
    }

    [DockerFact]
    public async Task The_primary_cannot_simply_be_switched_off()
    {
        using var admin = await SuperAdminAsync();

        var providers = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/providers");
        var primaryId = providers.EnumerateArray()
            .Single(p => p.GetProperty("role").GetString() == "Primary")
            .GetProperty("id").GetString();

        var response = await admin.PutAsJsonAsync(
            $"/api/admin/storage/providers/{primaryId}/role", new { role = "Inactive" });

        // Leaving no primary would leave nowhere to write.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Primary");
    }

    [DockerFact]
    public async Task A_sync_to_the_primary_itself_is_refused()
    {
        using var admin = await SuperAdminAsync();

        var providers = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/providers");
        var primaryId = providers.EnumerateArray()
            .Single(p => p.GetProperty("role").GetString() == "Primary")
            .GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync("/api/admin/storage/sync-jobs", new
        {
            mode = "Migration",
            targetId = primaryId,
            switchPrimaryOnComplete = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("eyni");
    }

    [DockerFact]
    public async Task An_unknown_sync_mode_is_refused()
    {
        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/storage/sync-jobs", new
        {
            mode = "Teleport",
            targetId = Guid.NewGuid(),
            switchPrimaryOnComplete = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task The_primary_cannot_be_deleted()
    {
        using var admin = await SuperAdminAsync();

        var providers = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/providers");
        var primaryId = providers.EnumerateArray()
            .Single(p => p.GetProperty("role").GetString() == "Primary")
            .GetProperty("id").GetString();

        var response = await admin.DeleteAsync($"/api/admin/storage/providers/{primaryId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Inactive");
    }

    [DockerFact]
    public async Task An_inactive_provider_nothing_uses_can_be_deleted()
    {
        using var admin = await SuperAdminAsync();

        var added = await admin.PostAsJsonAsync("/api/admin/storage/providers", Input($"Deletable {Guid.NewGuid():N}"));
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var response = await admin.DeleteAsync($"/api/admin/storage/providers/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        var providers = await admin.GetFromJsonAsync<JsonElement>("/api/admin/storage/providers");
        providers.EnumerateArray().Should().NotContain(p => p.GetProperty("id").GetString() == id);
    }

    [DockerFact]
    public async Task The_plan_routes_reach_their_handlers()
    {
        // Plan section 6 names these routes. An unknown id gets the handler's
        // 404 - with its message - rather than routing's bare one, which is
        // what proves the route exists.
        using var admin = await SuperAdminAsync();
        var unknown = Guid.NewGuid();

        foreach (var (path, expected) in new[]
                 {
                     ($"/api/admin/storage/providers/{unknown}/set-primary", "Storage provayderi"),
                     ($"/api/admin/storage/providers/{unknown}/set-secondary", "Storage provayderi"),
                     ($"/api/admin/storage/sync-jobs/{unknown}/retry-failed", "Sinxronizasiya işi"),
                     ($"/api/admin/storage/sync-jobs/{unknown}/purge-source", "Sinxronizasiya işi"),
                 })
        {
            var response = await admin.PostAsync(path, content: null);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await response.Content.ReadAsStringAsync()).Should().Contain(expected, path);
        }
    }

    [DockerFact]
    public async Task Nobody_without_storage_manage_gets_in()
    {
        using var anonymous = _fixture.CreateApiClient();

        (await anonymous.GetAsync("/api/admin/storage/providers")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        // A mobile user's token is a token, but not an admin one.
        (await anonymous.PostAsync("/api/admin/storage/rewrap", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}
