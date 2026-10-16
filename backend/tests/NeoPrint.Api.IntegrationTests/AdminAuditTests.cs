using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The admin panel's operations pages (step 7A, M9 - T-213): the audit log
/// and who a service task may be given to.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminAuditTests
{
    private const string Password = "AuditPagePassword!2026";

    private readonly NeoPrintApiFixture _fixture;

    public AdminAuditTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_audit_log_finds_an_action_by_its_name_and_its_kiosk_and_shows_a_scoped_account_its_own_kiosks_only()
    {
        if (Skip) { return; }

        using var mine = await TestKiosks.ActivatedAsync(_fixture, $"aud-{Guid.NewGuid():N}"[..20]);
        using var theirs = await TestKiosks.ActivatedAsync(_fixture, $"aud-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await admin.PostAsync($"/api/admin/kiosks/{mine.KioskId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/kiosks/{theirs.KioskId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/audit-logs?action=kiosks.command.&entityType=Kiosk&entityId={mine.KioskId}");
        var entry = page.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("action").GetString().Should().Be("kiosks.command.Pause");
        entry.GetProperty("actorType").GetString().Should().Be("Admin");
        entry.GetProperty("adminName").GetString().Should().NotBeNullOrWhiteSpace("the panel names who did it");
        page.GetProperty("total").GetInt64().Should().Be(1);

        // A prefix that names nothing finds nothing; a wrong range is refused.
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/audit-logs?action=no.such.&entityId={mine.KioskId}"))
            .GetProperty("total").GetInt64().Should().Be(0);
        (await admin.GetAsync("/api/admin/audit-logs?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // An account that looks after one kiosk reads what was done to it, and nothing else.
        using var scoped = await AccountAsync(KioskScope.Only(mine.KioskId), Permissions.AuditView);
        var seen = (await scoped.GetFromJsonAsync<JsonElement>("/api/admin/audit-logs?pageSize=200")).GetProperty("items").EnumerateArray().ToList();
        seen.Should().NotBeEmpty();
        seen.Should().OnlyContain(item => item.GetProperty("entityType").GetString() == "Kiosk"
                                          && item.GetProperty("entityId").GetString() == mine.KioskId.ToString());
        (await scoped.GetFromJsonAsync<JsonElement>($"/api/admin/audit-logs?entityId={theirs.KioskId}"))
            .GetProperty("total").GetInt64().Should().Be(0);

        using var blind = await AccountAsync(KioskScope.Everything(), Permissions.KiosksView);
        (await blind.GetAsync("/api/admin/audit-logs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await admin.PostAsync($"/api/admin/kiosks/{mine.KioskId}/resume", null);
        await admin.PostAsync($"/api/admin/kiosks/{theirs.KioskId}/resume", null);
    }

    [DockerFact]
    public async Task A_task_may_be_given_to_who_looks_after_the_kiosk_and_finishes_tasks_operators_first()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"asg-{Guid.NewGuid():N}"[..20]);
        using var other = await TestKiosks.ActivatedAsync(_fixture, $"asg-{Guid.NewGuid():N}"[..20]);

        var here = await AccountIdAsync(KioskScope.Only(kiosk.KioskId), "Here operator", Permissions.ServiceTasksComplete, Permissions.ServiceTasksView);
        var manager = await AccountIdAsync(KioskScope.Only(kiosk.KioskId), "Here manager", Permissions.ServiceTasksComplete, Permissions.ServiceTasksManage);
        var elsewhere = await AccountIdAsync(KioskScope.Only(other.KioskId), "Elsewhere operator", Permissions.ServiceTasksComplete);
        var looker = await AccountIdAsync(KioskScope.Only(kiosk.KioskId), "Only looks", Permissions.ServiceTasksView);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var list = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/service-tasks/assignees?kioskId={kiosk.KioskId}"))
            .EnumerateArray().ToList();
        var ids = list.Select(item => item.GetProperty("id").GetGuid()).ToList();

        ids.Should().Contain([here, manager]).And.NotContain([elsewhere, looker]);
        ids.IndexOf(here).Should().BeLessThan(ids.IndexOf(manager), "operators come first");
        list.Single(item => item.GetProperty("id").GetGuid() == here).GetProperty("operator").GetBoolean().Should().BeTrue();
        list.Single(item => item.GetProperty("id").GetGuid() == manager).GetProperty("operator").GetBoolean().Should().BeFalse();

        // An account that manages tasks of another kiosk cannot ask about this one.
        using var outsider = await AccountAsync(KioskScope.Only(other.KioskId), Permissions.ServiceTasksManage);
        (await outsider.GetAsync($"/api/admin/service-tasks/assignees?kioskId={kiosk.KioskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> AccountIdAsync(KioskScope scope, string name, params string[] permissions)
        => (await CreateAccountAsync(scope, name, permissions)).Id;

    private async Task<HttpClient> AccountAsync(KioskScope scope, params string[] permissions)
    {
        var account = await CreateAccountAsync(scope, "Audit page", permissions);
        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<AdminUser> CreateAccountAsync(KioskScope scope, string name, string[] permissions)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Audit role {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var services = _fixture.Services.CreateScope();
        var hasher = services.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();
        var account = new AdminUser
        {
            Email = $"audit.{Guid.NewGuid():N}@neoprint.test",
            FullName = name,
            RoleIds = [(await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()],
            KioskScope = scope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, Password);
        await services.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        return account;
    }
}
