using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The kiosk pages' API (plan sections 6 "Admin Kiosks", 8A.3 "Kiosk
/// yarat/redaktə").
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskAdminApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskAdminApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private static bool Skip => new DockerFactAttribute().Skip is not null;

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

    private static object NewKiosk(string? code = null, string pin = "482915", int? queue = 5) => new
    {
        name = $"Test kiosk {Guid.NewGuid():N}"[..24],
        code,
        lat = 40.4093,
        lng = 49.8671,
        address = "Nizami küç. 10, Bakı",
        defaultLanguage = "az",
        queueDisplayLimit = queue,
        maxFileSizeMb = (int?)null,
        maxPages = (int?)null,
        pricingProfileId = (Guid?)null,
        fiscalTerminalId = (string?)null,
        paperCapacityA4 = 500,
        paperCapacityA3 = 250,
        adminPin = pin,
    };

    /// <summary>The API leaves null properties out rather than writing them.</summary>
    private static bool Absent(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    private static async Task<JsonElement> CreateAsync(HttpClient admin, object? input = null)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/kiosks", input ?? NewKiosk());
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // -----------------------------------------------------------------------
    // Create
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_kiosk_is_created_without_a_device_and_gets_the_next_code()
    {
        using var admin = await SuperAdminAsync();

        var first = await CreateAsync(admin);
        var second = await CreateAsync(admin);

        first.GetProperty("code").GetString().Should().MatchRegex("^KSK-[0-9]{4}$");
        first.GetProperty("status").GetString().Should().Be("NotActivated");
        Absent(first, "activeDeviceId").Should().BeTrue("null is not written");
        first.GetProperty("hasAdminPin").GetBoolean().Should().BeTrue();
        first.GetProperty("paper").GetProperty("A4").GetProperty("capacity").GetInt32().Should().Be(500);
        first.GetProperty("paper").GetProperty("A3").GetProperty("capacity").GetInt32().Should().Be(250);

        // Codes are consecutive.
        var number = int.Parse(first.GetProperty("code").GetString()![4..]);
        second.GetProperty("code").GetString().Should().Be($"KSK-{number + 1:D4}");

        // No limits of its own: the installation defaults apply.
        var size = first.GetProperty("maxFileSizeMb");
        Absent(size, "override").Should().BeTrue();
        size.GetProperty("effective").GetInt32().Should().Be(size.GetProperty("default").GetInt32());
    }

    [DockerFact]
    public async Task The_admin_pin_is_never_returned_or_logged()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/kiosks", NewKiosk(pin: "731904"));
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);
        raw.Should().NotContain("731904").And.NotContain("adminPinHash");

        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        (await admin.PostAsJsonAsync($"/api/admin/kiosks/{id}/admin-pin", new { pin = "560213" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var entries = await context.AuditLogs.Find(entry => entry.EntityId == id).ToListAsync();
        entries.Select(entry => entry.Action).Should().Contain(["kiosks.create", "kiosks.admin-pin"]);

        var kiosk = await context.Kiosks.Find(k => k.Id == Guid.Parse(id!)).FirstAsync();

        foreach (var entry in entries)
        {
            $"{entry.Before}{entry.After}".Should()
                .NotContain("731904").And.NotContain("560213").And.NotContain(kiosk.AdminPinHash!);
        }

        // Stored as a hash, and the new one replaced the old.
        kiosk.AdminPinHash.Should().NotBeNullOrEmpty().And.NotContain("560213");
    }

    [DockerFact]
    public async Task Bad_input_is_refused_with_the_field_named()
    {
        using var admin = await SuperAdminAsync();

        foreach (var (input, field) in new (object Input, string Field)[]
                 {
                     (NewKiosk(pin: "12ab"), "adminPin"),
                     (NewKiosk(pin: "12"), "adminPin"),
                     (NewKiosk(queue: 4), "queueDisplayLimit"),
                     (NewKiosk(code: "K-1"), "code"),
                 })
        {
            var response = await admin.PostAsJsonAsync("/api/admin/kiosks", input);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, field);
            (await response.Content.ReadAsStringAsync()).Should().Contain(field);
        }
    }

    [DockerFact]
    public async Task A_code_in_use_is_refused()
    {
        using var admin = await SuperAdminAsync();

        var existing = await CreateAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/kiosks", NewKiosk(code: existing.GetProperty("code").GetString()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("artıq istifadə olunur");
    }

    // -----------------------------------------------------------------------
    // Edit
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Editing_a_kiosk_tells_it_to_fetch_its_configuration_again()
    {
        using var admin = await SuperAdminAsync();

        var kiosk = await CreateAsync(admin);
        var id = kiosk.GetProperty("id").GetString();
        var changedBefore = kiosk.GetProperty("configurationChangedAt").GetDateTime();

        var response = await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}", new
        {
            name = "Renamed kiosk",
            lat = 40.3777,
            lng = 49.8920,
            address = "28 May m/st, Bakı",
            defaultLanguage = "ru",
            queueDisplayLimit = 3,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();

        updated.GetProperty("name").GetString().Should().Be("Renamed kiosk");
        updated.GetProperty("defaultLanguage").GetString().Should().Be("ru");
        updated.GetProperty("queueDisplayLimit").GetInt32().Should().Be(3);
        updated.GetProperty("lat").GetDouble().Should().Be(40.3777);

        // The heartbeat returns this, so the kiosk knows to reload.
        updated.GetProperty("configurationChangedAt").GetDateTime().Should().BeAfter(changedBefore);
    }

    [DockerFact]
    public async Task Limits_override_the_default_until_set_back_to_it()
    {
        using var admin = await SuperAdminAsync();

        var id = (await CreateAsync(admin)).GetProperty("id").GetString();

        var set = await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}/limits", new { maxFileSizeMb = 20, maxPages = 80 });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var overridden = await set.Content.ReadFromJsonAsync<JsonElement>();

        overridden.GetProperty("maxFileSizeMb").GetProperty("effective").GetInt32().Should().Be(20);
        overridden.GetProperty("maxPages").GetProperty("effective").GetInt32().Should().Be(80);

        // "Default istifadə et".
        var cleared = await (await admin.PutAsJsonAsync(
                $"/api/admin/kiosks/{id}/limits", new { maxFileSizeMb = (int?)null, maxPages = (int?)null }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var size = cleared.GetProperty("maxFileSizeMb");
        Absent(size, "override").Should().BeTrue();
        size.GetProperty("effective").GetInt32().Should().Be(size.GetProperty("default").GetInt32());

        (await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}/limits", new { maxFileSizeMb = 0, maxPages = 10 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_pricing_profile_must_exist_to_be_assigned()
    {
        using var admin = await SuperAdminAsync();

        var id = (await CreateAsync(admin)).GetProperty("id").GetString();

        (await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}/pricing-profile", new { pricingProfileId = Guid.NewGuid() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var profiles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/pricing-profiles");
        var profileId = profiles.EnumerateArray().First().GetProperty("id").GetGuid();

        var assigned = await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}/pricing-profile", new { pricingProfileId = profileId });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());
        (await assigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pricingProfileId").GetGuid().Should().Be(profileId);
    }

    [DockerFact]
    public async Task Lowering_a_paper_capacity_brings_the_counter_down_with_it()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var id = Guid.Parse((await CreateAsync(admin)).GetProperty("id").GetString()!);

        // As if the tray had been filled.
        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            await context.Kiosks.UpdateOneAsync(
                Builders<Kiosk>.Filter.Eq(k => k.Id, id),
                Builders<Kiosk>.Update.Set("PaperStock.A4.Current", 450));
        }

        var response = await admin.PutAsJsonAsync($"/api/admin/kiosks/{id}/paper/capacity", new { a4 = 300, a3 = 250 });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var a4 = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paper").GetProperty("A4");
        a4.GetProperty("capacity").GetInt32().Should().Be(300);
        a4.GetProperty("current").GetInt32().Should().Be(300, "more sheets than the tray holds is not a count");
    }

    // -----------------------------------------------------------------------
    // Delete and devices
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Only_a_kiosk_that_never_had_a_device_can_be_deleted()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var fresh = (await CreateAsync(admin)).GetProperty("id").GetString();
        (await admin.DeleteAsync($"/api/admin/kiosks/{fresh}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/admin/kiosks/{fresh}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A kiosk that once had a device, since revoked: jobs and counters
        // refer to it, so it stays.
        var used = Guid.Parse((await CreateAsync(admin)).GetProperty("id").GetString()!);

        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            await context.KioskDevices.InsertOneAsync(new KioskDevice
            {
                PublicKey = $"test-key-{Guid.NewGuid():N}",
                LastKioskId = used,
            });
        }

        var refused = await admin.DeleteAsync($"/api/admin/kiosks/{used}");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("tarixçəsi");

        // And the device shows in its history.
        var devices = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{used}/devices");
        devices.GetArrayLength().Should().Be(1);
        devices[0].GetProperty("isActive").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task A_scoped_operator_cannot_reach_a_kiosk_outside_the_scope()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var inside = Guid.Parse((await CreateAsync(admin)).GetProperty("id").GetString()!);
        var outside = Guid.Parse((await CreateAsync(admin)).GetProperty("id").GetString()!);

        var role = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Kiosk editor {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions = new[] { Permissions.KiosksView, Permissions.KiosksEdit },
        });
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scoped = await ScopedAdminAsync(roleId, KioskScope.Only(inside));

        (await scoped.GetAsync($"/api/admin/kiosks/{inside}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Not found rather than forbidden: the answer does not confirm the
        // kiosk exists.
        (await scoped.GetAsync($"/api/admin/kiosks/{outside}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.PostAsJsonAsync($"/api/admin/kiosks/{outside}/admin-pin", new { pin = "999999" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And the permission still counts: this role may not create kiosks.
        (await scoped.PostAsJsonAsync("/api/admin/kiosks", NewKiosk())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<HttpClient> ScopedAdminAsync(Guid roleId, KioskScope kioskScope)
    {
        const string password = "ScopedAdminPassword!2026";
        var email = $"kiosk.scope.{Guid.NewGuid():N}@neoprint.test";

        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            var hasher = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

            var account = new AdminUser
            {
                Email = email,
                FullName = "Scoped operator",
                RoleIds = [roleId],
                KioskScope = kioskScope,
                IsActive = true,
            };
            account.PasswordHash = hasher.HashPassword(account, password);

            await context.AdminUsers.InsertOneAsync(account);
        }

        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }
}
