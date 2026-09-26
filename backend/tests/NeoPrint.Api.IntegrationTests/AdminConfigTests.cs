using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Administrator accounts, settings and legal documents
/// (plan sections 2.4, 2.13, 8A.2, 8A.3).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminConfigTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminConfigTests(NeoPrintApiFixture fixture) => _fixture = fixture;

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

    private async Task<Guid> OperatorRoleIdAsync(HttpClient admin)
    {
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");

        return roles.EnumerateArray()
            .First(role => role.GetProperty("isSystem").GetBoolean() == false)
            .GetProperty("id")
            .GetGuid();
    }

    // -----------------------------------------------------------------------
    // Administrator accounts
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task An_account_is_created_and_has_to_change_its_password()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var email = $"operator.{Guid.NewGuid():N}@neoprint.test";

        var created = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email,
            fullName = "Yeni operator",
            password = "OperatorPassword!2026",
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("email").GetString().Should().Be(email);
        body.GetProperty("isActive").GetBoolean().Should().BeTrue();

        // Whoever created the account chose the password, so the holder
        // replaces it before doing anything (plan section 2.8).
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();

        // And no hash leaves the server.
        (await created.Content.ReadAsStringAsync()).Should().NotContain("passwordHash");
    }

    [DockerFact]
    public async Task The_new_account_can_sign_in_and_is_held_at_the_password_screen()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var email = $"operator.{Guid.NewGuid():N}@neoprint.test";
        const string password = "OperatorPassword!2026";

        await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email,
            fullName = "Signs in",
            password,
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        using var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var session = await login.Content.ReadFromJsonAsync<JsonElement>();

        session.GetProperty("admin").GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task An_account_with_no_role_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email = $"roleless.{Guid.NewGuid():N}@neoprint.test",
            fullName = "No role",
            password = "OperatorPassword!2026",
            roleIds = Array.Empty<Guid>(),
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        // An account that can sign in and do nothing is a confusing thing to
        // create by accident.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task A_scoped_account_needs_at_least_one_kiosk()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email = $"scopeless.{Guid.NewGuid():N}@neoprint.test",
            fullName = "Empty scope",
            password = "OperatorPassword!2026",
            roleIds = new[] { roleId },
            allKiosks = false,
            kioskIds = Array.Empty<Guid>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_short_password_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var response = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email = $"weak.{Guid.NewGuid():N}@neoprint.test",
            fullName = "Weak password",
            password = "short",
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task The_same_email_cannot_be_used_twice()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var email = $"duplicate.{Guid.NewGuid():N}@neoprint.test";

        object Body() => new
        {
            email,
            fullName = "Duplicate",
            password = "OperatorPassword!2026",
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        };

        (await admin.PostAsJsonAsync("/api/admin/admins", Body()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsJsonAsync("/api/admin/admins", Body()))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Deactivating_an_account_ends_its_session_at_once()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var email = $"deactivate.{Guid.NewGuid():N}@neoprint.test";
        const string password = "OperatorPassword!2026";

        var created = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email,
            fullName = "To be disabled",
            password,
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var theirs = _fixture.CreateApiClient();
        var login = await theirs.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();

        theirs.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", session.GetProperty("accessToken").GetString());

        var deactivate = await admin.PostAsJsonAsync(
            $"/api/admin/admins/{id}/active", new { isActive = false, reason = "Left the company" });

        deactivate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The token is still inside its fifteen minutes, but the account
        // resolves to no permissions at all.
        (await theirs.GetAsync("/api/admin/me")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task The_last_active_super_admin_cannot_be_deactivated()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var me = await admin.GetFromJsonAsync<JsonElement>("/api/admin/me");
        var myId = me.GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync(
            $"/api/admin/admins/{myId}/active", new { isActive = false, reason = "Locking myself out" });

        // Otherwise the installation has nobody who can put it back, short of
        // editing the database (plan section 8A.2).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().Should().Contain("SuperAdmin");
    }

    [DockerFact]
    public async Task Resetting_a_password_ends_every_session_and_forces_a_change()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var roleId = await OperatorRoleIdAsync(admin);

        var email = $"reset.{Guid.NewGuid():N}@neoprint.test";
        const string password = "OperatorPassword!2026";

        var created = await admin.PostAsJsonAsync("/api/admin/admins", new
        {
            email,
            fullName = "Forgot the password",
            password,
            roleIds = new[] { roleId },
            allKiosks = true,
            kioskIds = Array.Empty<Guid>(),
        });

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var theirs = _fixture.CreateApiClient();
        var login = await theirs.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();

        if (login.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            theirs.DefaultRequestHeaders.Add(
                "Cookie", string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0])));
        }

        theirs.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", session.GetProperty("accessToken").GetString());

        var reset = await admin.PostAsJsonAsync(
            $"/api/admin/admins/{id}/reset-password", new { newPassword = "BrandNewPassword!2026" });

        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A stolen session must not outlive the reset, and the refusal says
        // 401: the request was well formed, the session is simply over. A 400
        // would tell a client its request was malformed and invite a retry.
        var refused = await theirs.PostAsync("/api/auth/admin/refresh", content: null);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The new password works, and has to be changed.
        using var afterReset = _fixture.CreateApiClient();
        var again = await afterReset.PostAsJsonAsync(
            "/api/auth/admin/login", new { email, password = "BrandNewPassword!2026" });

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());

        (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("admin").GetProperty("mustChangePassword").GetBoolean()
            .Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Settings_are_listed_with_their_documented_defaults()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/admin/settings");
        var all = settings.EnumerateArray().ToList();

        all.Should().HaveCountGreaterThan(40);

        var queueLimit = all.Single(setting => setting.GetProperty("key").GetString() == "Queue.DisplayLimit");

        queueLimit.GetProperty("defaultValue").GetString().Should().Be("5");
        queueLimit.GetProperty("valueType").GetString().Should().Be("Integer");
        queueLimit.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task A_changed_setting_takes_effect_and_says_it_is_no_longer_the_default()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var updated = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            values = new Dictionary<string, string> { ["Queue.DisplayLimit"] = "3" },
        });

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        var settings = await updated.Content.ReadFromJsonAsync<JsonElement>();
        var queueLimit = settings.EnumerateArray()
            .Single(setting => setting.GetProperty("key").GetString() == "Queue.DisplayLimit");

        queueLimit.GetProperty("value").GetString().Should().Be("3");
        queueLimit.GetProperty("isDefault").GetBoolean().Should().BeFalse();

        // The reader sees it, which means the cache entry was cleared rather
        // than left holding the old value.
        using var scope = _fixture.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISettingsReader>();

        (await reader.GetIntAsync(SettingKeys.QueueDisplayLimit, 5)).Should().Be(3);

        // Put it back, so the other tests see the seeded value.
        await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            values = new Dictionary<string, string> { ["Queue.DisplayLimit"] = "5" },
        });
    }

    [DockerFact]
    public async Task An_unknown_setting_is_refused_before_anything_is_written()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            values = new Dictionary<string, string> { ["Nonsense.Key"] = "1" },
        });

        // Otherwise it sits in the collection forever with nothing reading it.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_value_of_the_wrong_type_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            values = new Dictionary<string, string> { ["Queue.DisplayLimit"] = "five" },
        });

        // It would only fail later, at the point something tried to use it.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var stored = await context.AdminSettings
            .Find(setting => setting.Key == SettingKeys.QueueDisplayLimit)
            .FirstAsync();

        stored.Value.Should().Be("5", "nothing is written when the request is refused");
    }

    [DockerFact]
    public async Task Changing_a_setting_is_audited_one_entry_per_setting()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            values = new Dictionary<string, string> { ["Kiosk.IdleResetSeconds"] = "90" },
        });

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var entries = await context.AuditLogs
            .Find(entry => entry.Action == "settings.update" && entry.EntityId == "Kiosk.IdleResetSeconds")
            .ToListAsync();

        // So the log answers "who changed the idle timeout", not "somebody
        // saved the settings page".
        entries.Should().NotBeEmpty();
        entries[^1].After.Should().Be("90");
    }

    // -----------------------------------------------------------------------
    // Legal documents
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_seeded_drafts_are_listed_as_current()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var documents = await admin.GetFromJsonAsync<JsonElement>("/api/admin/legal-documents");
        var all = documents.EnumerateArray().ToList();

        all.Should().NotBeEmpty();
        all.Should().Contain(document => document.GetProperty("isCurrent").GetBoolean());
    }

    [DockerFact]
    public async Task Publishing_a_version_makes_it_the_one_users_are_shown()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var version = $"test-{Guid.NewGuid():N}"[..12];

        var published = await admin.PostAsJsonAsync("/api/admin/legal-documents", new
        {
            docType = "Privacy",
            version,
            language = "az",
            content = "# Məxfilik siyasəti\n\nTest versiyası.",
        });

        published.StatusCode.Should().Be(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());

        var body = await published.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        body.GetProperty("acceptances").GetInt64().Should().Be(0);

        // What an anonymous caller is now shown.
        using var anonymous = _fixture.CreateApiClient();
        var current = await anonymous.GetFromJsonAsync<JsonElement>("/api/legal/Privacy?lang=az");

        current.GetProperty("version").GetString().Should().Be(version);
    }

    [DockerFact]
    public async Task The_same_version_cannot_be_published_twice()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var version = $"dup-{Guid.NewGuid():N}"[..10];

        object Body() => new
        {
            docType = "Terms",
            version,
            language = "az",
            content = "# İstifadə şərtləri",
        };

        (await admin.PostAsJsonAsync("/api/admin/legal-documents", Body()))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await admin.PostAsJsonAsync("/api/admin/legal-documents", Body());

        // Republishing the same version with different text would change what
        // people already accepted, retrospectively.
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task An_unsupported_language_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/legal-documents", new
        {
            docType = "Privacy",
            version = "1.0",
            language = "de",
            content = "# Datenschutz",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------------
    // Permissions
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_new_endpoints_need_their_own_permissions()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        foreach (var path in new[]
                 {
                     "/api/admin/admins", "/api/admin/settings", "/api/admin/legal-documents",
                 })
        {
            (await client.GetAsync(path)).StatusCode.Should().Be(
                HttpStatusCode.Unauthorized, $"{path} must not be readable anonymously");
        }
    }
}
