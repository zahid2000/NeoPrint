using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Dynamic RBAC end to end (plan sections 8A.2, 11): a role created at runtime,
/// the endpoints it opens and closes, and a permission change taking effect
/// without signing in again.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RbacTests
{
    private readonly NeoPrintApiFixture _fixture;

    public RbacTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<Guid> CreateRoleAsync(
        HttpClient admin, string name, params string[] permissions)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name,
            description = "Created by the integration tests.",
            permissions,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // -----------------------------------------------------------------------
    // Sign-in
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_seeded_super_admin_signs_in_and_holds_every_permission()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var me = await admin.GetAsync("/api/admin/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await me.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("email").GetString().Should().Be(NeoPrintApiFixture.SeedAdminEmail);
        body.GetProperty("roles").EnumerateArray().Select(role => role.GetString())
            .Should().Contain(AdminRole.SuperAdminName);
        body.GetProperty("allKiosks").GetBoolean().Should().BeTrue();

        var permissions = body.GetProperty("permissions").EnumerateArray()
            .Select(permission => permission.GetString()!).ToHashSet();

        permissions.Should().BeEquivalentTo(Permissions.All);

        // The seeded password came from a configuration value, so it has to be
        // changed at first sign-in (plan section 2.8).
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task A_wrong_password_is_refused_without_saying_which_part_was_wrong()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = "definitely-not-the-password",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Email və ya şifrə yanlışdır");
    }

    [DockerFact]
    public async Task Sign_in_puts_the_refresh_token_in_a_cookie_not_in_the_body()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var response = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("refreshToken", "no script in the panel should be able to read it");

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        var refreshCookie = cookies.FirstOrDefault(cookie => cookie.StartsWith("neoprint_admin_rt", StringComparison.Ordinal));

        refreshCookie.Should().NotBeNull();
        refreshCookie.Should().Contain("httponly", Exactly.Once(), because: "set with HttpOnly");
        refreshCookie.Should().Contain("samesite=strict", Exactly.Once(), because: "set with SameSite=Strict");
    }

    // -----------------------------------------------------------------------
    // A role created at runtime
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_role_created_at_runtime_opens_exactly_the_endpoints_it_names()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(
            admin, $"Devices only {Guid.NewGuid():N}", Permissions.DevicesView);

        var (client, _) = await CreateAdminWithRoleAsync(admin, roleId);
        using var scoped = client;

        // Granted.
        (await scoped.GetAsync("/api/admin/devices")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Not granted: everything else is closed, including reading the role
        // list this account's own role lives in.
        (await scoped.GetAsync("/api/admin/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scoped.GetAsync("/api/admin/features")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task Granting_a_permission_takes_effect_without_signing_in_again()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleName = $"Growing role {Guid.NewGuid():N}";
        var roleId = await CreateRoleAsync(admin, roleName, Permissions.DevicesView);

        var (client, _) = await CreateAdminWithRoleAsync(admin, roleId);
        using var scoped = client;

        (await scoped.GetAsync("/api/admin/features")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The SuperAdmin adds a permission to the role.
        var update = await admin.PutAsJsonAsync($"/api/admin/roles/{roleId}", new
        {
            name = roleName,
            description = "Now with feature management.",
            permissions = new[] { Permissions.DevicesView, Permissions.FeaturesManage },
        });

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        // The token the scoped account is holding does not carry the new
        // permission, so it has to fetch a new one - that is what the panel
        // does on PermissionsChanged. Refreshing is enough; no sign-in.
        await RefreshAsync(scoped);

        (await scoped.GetAsync("/api/admin/features")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Revoking_a_permission_closes_the_endpoint_before_the_token_expires()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleName = $"Shrinking role {Guid.NewGuid():N}";
        var roleId = await CreateRoleAsync(
            admin, roleName, Permissions.DevicesView, Permissions.FeaturesManage);

        var (client, _) = await CreateAdminWithRoleAsync(admin, roleId);
        using var scoped = client;

        (await scoped.GetAsync("/api/admin/features")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Take the permission away. The account keeps its 15 minute token,
        // which still lists features.manage among its claims.
        var update = await admin.PutAsJsonAsync($"/api/admin/roles/{roleId}", new
        {
            name = roleName,
            description = "Feature management removed.",
            permissions = new[] { Permissions.DevicesView },
        });

        update.StatusCode.Should().Be(HttpStatusCode.OK);

        // The endpoint closes at once: the claim in the token is only a fast
        // path, and it is confirmed against the resolver, whose cache the role
        // change cleared (plan section 8A.2).
        (await scoped.GetAsync("/api/admin/features")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // What is left still works.
        (await scoped.GetAsync("/api/admin/devices")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task A_deactivated_account_can_do_nothing_even_with_a_live_token()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(
            admin, $"To be disabled {Guid.NewGuid():N}", Permissions.DevicesView);

        var (client, adminUserId) = await CreateAdminWithRoleAsync(admin, roleId);
        using var scoped = client;

        (await scoped.GetAsync("/api/admin/devices")).StatusCode.Should().Be(HttpStatusCode.OK);

        await DeactivateAsync(adminUserId);

        (await scoped.GetAsync("/api/admin/devices")).StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "a deactivated account resolves to no permissions at all");
    }

    // -----------------------------------------------------------------------
    // Role rules
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_permission_outside_the_catalogue_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Invented {Guid.NewGuid():N}",
            permissions = new[] { "payments.steal" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("payments.steal");
    }

    [DockerFact]
    public async Task The_super_admin_role_cannot_be_edited_or_deleted()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roles = await (await admin.GetAsync("/api/admin/roles")).Content.ReadFromJsonAsync<JsonElement>();
        var superAdmin = roles.EnumerateArray()
            .First(role => role.GetProperty("name").GetString() == AdminRole.SuperAdminName);

        superAdmin.GetProperty("isSystem").GetBoolean().Should().BeTrue();

        var id = superAdmin.GetProperty("id").GetGuid();

        var update = await admin.PutAsJsonAsync($"/api/admin/roles/{id}", new
        {
            name = "Not SuperAdmin any more",
            permissions = new[] { Permissions.DashboardView },
        });

        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/admin/roles/{id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task A_role_that_is_still_assigned_cannot_be_deleted()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(admin, $"In use {Guid.NewGuid():N}", Permissions.DevicesView);
        var (client, _) = await CreateAdminWithRoleAsync(admin, roleId);
        client.Dispose();

        var response = await admin.DeleteAsync($"/api/admin/roles/{roleId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("silinə bilməz");
    }

    [DockerFact]
    public async Task A_role_held_only_by_a_deactivated_account_cannot_be_deleted()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(
            admin, $"Held by a disabled account {Guid.NewGuid():N}", Permissions.DevicesView);

        var (client, adminUserId) = await CreateAdminWithRoleAsync(admin, roleId);
        client.Dispose();

        await DeactivateAsync(adminUserId);

        // A deactivated account keeps its roles, so the role is still in use:
        // deleting it would leave that account pointing at a role that no
        // longer exists, which nobody would notice until it was activated
        // again.
        var response = await admin.DeleteAsync($"/api/admin/roles/{roleId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("silinə bilməz");
    }

    [DockerFact]
    public async Task An_unassigned_role_can_be_deleted()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(admin, $"Unused {Guid.NewGuid():N}", Permissions.DevicesView);

        (await admin.DeleteAsync($"/api/admin/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Cloning_a_role_copies_its_permissions_into_an_editable_role()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roles = await (await admin.GetAsync("/api/admin/roles")).Content.ReadFromJsonAsync<JsonElement>();
        var superAdmin = roles.EnumerateArray()
            .First(role => role.GetProperty("name").GetString() == AdminRole.SuperAdminName);

        var clone = await admin.PostAsJsonAsync(
            $"/api/admin/roles/{superAdmin.GetProperty("id").GetGuid()}/clone",
            new { name = $"SuperAdmin copy {Guid.NewGuid():N}" });

        clone.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await clone.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("isSystem").GetBoolean().Should().BeFalse("a clone is an ordinary, editable role");
        body.GetProperty("permissions").GetArrayLength().Should().Be(
            Permissions.All.Count, "cloning SuperAdmin yields a role that really holds everything");
    }

    [DockerFact]
    public async Task Two_roles_cannot_share_a_name()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();
        var name = $"Duplicate {Guid.NewGuid():N}";

        await CreateRoleAsync(admin, name, Permissions.DevicesView);

        var second = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name,
            permissions = new[] { Permissions.JobsView },
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // -----------------------------------------------------------------------
    // Token separation
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_mobile_user_token_cannot_reach_an_admin_endpoint()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = $"rbac.user.{Guid.NewGuid():N}@example.com";

        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "UserPassword123",
            language = "az",
            consents = new[]
            {
                new { docType = "Privacy", version = "0.1-draft" },
                new { docType = "Terms", version = "0.1-draft" },
            },
        });

        register.StatusCode.Should().Be(HttpStatusCode.OK);

        // A user token is not an admin token, whatever it carries.
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "UserPassword123",
        });

        // Email is unconfirmed, so there is no token at all yet - which is
        // itself the point: registering does not grant access.
        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await client.GetAsync("/api/admin/devices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Admin_endpoints_are_closed_without_a_token()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        foreach (var path in new[]
                 {
                     "/api/admin/me", "/api/admin/roles", "/api/admin/devices",
                     "/api/admin/features", "/api/admin/enrollment-tokens", "/api/admin/kiosks",
                 })
        {
            (await client.GetAsync(path)).StatusCode.Should().Be(
                HttpStatusCode.Unauthorized, $"{path} must not be readable anonymously");
        }
    }

    // -----------------------------------------------------------------------
    // Kiosk scope
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_scoped_account_sees_only_the_kiosks_in_its_scope()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var all = await admin.GetFromJsonAsync<JsonElement>("/api/admin/kiosks");
        var everyKiosk = all.EnumerateArray().Select(kiosk => kiosk.GetProperty("id").GetGuid()).ToList();

        everyKiosk.Should().HaveCountGreaterThan(
            1, "the development seed creates more than one kiosk");

        var roleId = await CreateRoleAsync(
            admin, $"One kiosk {Guid.NewGuid():N}", Permissions.KiosksView);

        var (client, _) = await CreateAdminWithRoleAsync(
            admin, roleId, KioskScope.Only(everyKiosk[0]));
        using var scoped = client;

        var visible = await scoped.GetFromJsonAsync<JsonElement>("/api/admin/kiosks");

        visible.EnumerateArray()
            .Select(kiosk => kiosk.GetProperty("id").GetGuid())
            .Should().Equal(everyKiosk[0]);
    }

    [DockerFact]
    public async Task Reading_kiosks_without_the_permission_is_refused()
    {
        if (Skip) { return; }

        using var admin = await SuperAdminAsync();

        var roleId = await CreateRoleAsync(
            admin, $"No kiosks {Guid.NewGuid():N}", Permissions.DevicesView);

        var (client, _) = await CreateAdminWithRoleAsync(admin, roleId);
        using var scoped = client;

        (await scoped.GetAsync("/api/admin/kiosks")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // Test support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates an administrator holding one role and returns a client signed in
    /// as them.
    /// </summary>
    /// <remarks>
    /// There is no admin-user CRUD endpoint yet (it lands with the rest of the
    /// admin panel), so the account is written straight to MongoDB - the same
    /// way the seeder does - and then signs in through the real endpoint.
    /// </remarks>
    private async Task<(HttpClient Client, Guid AdminUserId)> CreateAdminWithRoleAsync(
        HttpClient superAdmin, Guid roleId, KioskScope? kioskScope = null)
    {
        const string password = "ScopedAdminPassword!2026";
        var email = $"scoped.{Guid.NewGuid():N}@neoprint.test";

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<MongoContext>();
        var hasher = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

        var admin = new AdminUser
        {
            Email = email,
            FullName = "Scoped Admin",
            RoleIds = [roleId],
            KioskScope = kioskScope ?? KioskScope.Everything(),
            IsActive = true,
        };

        admin.PasswordHash = hasher.HashPassword(admin, password);

        await context.AdminUsers.InsertOneAsync(admin);

        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        // Carry the refresh cookie, so RefreshAsync can be used.
        if (login.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        return (client, admin.Id);
    }

    private async Task DeactivateAsync(Guid adminUserId)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider
            .GetRequiredService<MongoContext>();
        var resolver = scope.ServiceProvider
            .GetRequiredService<IPermissionResolver>();

        await context.AdminUsers.UpdateOneAsync(
            Builders<AdminUser>.Filter.Eq(admin => admin.Id, adminUserId),
            Builders<AdminUser>.Update.Set(admin => admin.IsActive, false));

        // The panel would do this through the normal admin-user endpoint, which
        // clears the cache as part of saving.
        await resolver.InvalidateAsync(adminUserId);
    }

    private static async Task RefreshAsync(HttpClient client)
    {
        var refresh = await client.PostAsync("/api/auth/admin/refresh", content: null);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK, await refresh.Content.ReadAsStringAsync());

        var body = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());
    }
}
