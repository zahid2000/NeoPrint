using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Incidents;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// What an open panel hears over <c>/hubs/admin</c> when a kiosk's status
/// changes (plan section 6), over a real hub connection.
/// </summary>
/// <remarks>
/// A panel left open kept showing a kiosk as it was when the page loaded
/// (T-248). Part of that was the panel; the other part is here: every way a
/// kiosk changes status has to be announced, and an account that looks after
/// some kiosks must hear about those and no others (T-245).
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdminLiveEventsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly NeoPrintApiFixture _fixture;

    public AdminLiveEventsTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_kiosk_that_goes_offline_and_comes_back_is_announced_both_times()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "live-offline");
        await BeatAsync(kiosk);

        await using var panel = await RawHubConnection.ConnectAsync(_fixture, "admin", await SuperAdminTokenAsync());

        await GoSilentAsync(kiosk);

        (await StatusOfAsync(panel, kiosk.KioskId)).Should().Be("Offline");

        // The kiosk is switched on again: its first heartbeat.
        await BeatAsync(kiosk);

        (await StatusOfAsync(panel, kiosk.KioskId)).Should().Be("Online", "a panel that is open must not need a reload to see the kiosk back");
    }

    [DockerFact]
    public async Task A_pause_a_resume_and_a_released_device_are_announced_like_any_other_change()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "live-pause");
        await BeatAsync(kiosk);

        await using var panel = await RawHubConnection.ConnectAsync(_fixture, "admin", await SuperAdminTokenAsync());

        (await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(panel, kiosk.KioskId)).Should().Be("Maintenance");

        (await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/resume", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(panel, kiosk.KioskId)).Should().Be("Online");

        var revoke = await admin.PostAsJsonAsync($"/api/admin/devices/{kiosk.DeviceId}/revoke", new { reason = "Replaced" });
        revoke.IsSuccessStatusCode.Should().BeTrue(await revoke.Content.ReadAsStringAsync());
        (await StatusOfAsync(panel, kiosk.KioskId)).Should().Be("NotActivated");
    }

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_hears_about_that_kiosk_and_no_other()
    {
        if (Skip) { return; }

        using var mine = await ReadyKioskAsync(_fixture, "live-mine");
        using var theirs = await ReadyKioskAsync(_fixture, "live-theirs");
        await BeatAsync(mine);
        await BeatAsync(theirs);

        var (scopedToken, _) = await ScopedAdminAsync(KioskScope.Only(mine.KioskId), Permissions.KiosksView, Permissions.IncidentsView);

        await using var everything = await RawHubConnection.ConnectAsync(_fixture, "admin", await SuperAdminTokenAsync());
        await using var scoped = await RawHubConnection.ConnectAsync(_fixture, "admin", scopedToken);

        // The other kiosk first. The account with every kiosk hears all of
        // it - the status, then the incident - which also says both messages
        // have been sent before anything of "mine" is.
        await GoSilentAsync(theirs);
        await everything.ReadUntilAsync((target, argument) => target == "IncidentRaised" && KioskOf(argument) == theirs.KioskId, Wait);

        await GoSilentAsync(mine);

        // Everything the scoped panel was sent, up to its own kiosk's incident.
        var heard = await scoped.ReadUntilAsync(
            (target, argument) => target == "IncidentRaised" && KioskOf(argument) == mine.KioskId, Wait);

        heard.Where(item => KioskOf(item.Argument) is not null)
            .Should().OnlyContain(item => KioskOf(item.Argument) == mine.KioskId, "another kiosk's events are not this account's to see");

        heard.Should().Contain(
            item => item.Target == "KioskStatusChanged" && item.Argument.GetProperty("status").GetString() == "Offline",
            "its own kiosk's status arrives live");
        heard[^1].Argument.GetProperty("type").GetString().Should().Be("ConnectionLost");

        // And back: both return, the scoped panel hears of its own only.
        await BeatAsync(theirs);
        await everything.ReadUntilAsync((target, argument) => target == "IncidentResolved" && KioskOf(argument) == theirs.KioskId, Wait);

        await BeatAsync(mine);

        var back = await scoped.ReadUntilAsync(
            (target, argument) => target == "IncidentResolved" && KioskOf(argument) == mine.KioskId, Wait);

        back.Where(item => KioskOf(item.Argument) is not null).Should().OnlyContain(item => KioskOf(item.Argument) == mine.KioskId);
        back.Should().Contain(
            item => item.Target == "KioskStatusChanged" && item.Argument.GetProperty("status").GetString() == "Online");
    }

    [DockerFact]
    public async Task A_panel_left_open_stops_hearing_a_kiosk_that_is_taken_out_of_its_accounts_scope()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var first = await ReadyKioskAsync(_fixture, "live-scope-first");
        using var second = await ReadyKioskAsync(_fixture, "live-scope-second");
        await BeatAsync(first);
        await BeatAsync(second);

        var (token, account) = await ScopedAdminAsync(KioskScope.Only(first.KioskId), Permissions.KiosksView, Permissions.IncidentsView);

        await using var everything = await RawHubConnection.ConnectAsync(_fixture, "admin", await SuperAdminTokenAsync());
        await using var open = await RawHubConnection.ConnectAsync(_fixture, "admin", token);

        // The account is moved from the first kiosk to the second.
        var update = await admin.PutAsJsonAsync($"/api/admin/admins/{account.Id}", new
        {
            fullName = account.FullName,
            roleIds = account.RoleIds,
            allKiosks = false,
            kioskIds = new[] { second.KioskId },
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        (await open.ClosedByServerAsync(Wait)).Should().BeTrue(
            "the connection was put into the first kiosk's group when it opened, and would stay there");

        // The panel connects again by itself - with the token it already has.
        await using var again = await RawHubConnection.ConnectAsync(_fixture, "admin", token);

        await GoSilentAsync(first);
        await everything.ReadUntilAsync((target, argument) => target == "IncidentRaised" && KioskOf(argument) == first.KioskId, Wait);

        await GoSilentAsync(second);

        var heard = await again.ReadUntilAsync(
            (target, argument) => target == "IncidentRaised" && KioskOf(argument) == second.KioskId, Wait);

        heard.Where(item => KioskOf(item.Argument) is not null)
            .Should().OnlyContain(item => KioskOf(item.Argument) == second.KioskId, "the first kiosk is no longer this account's");
    }

    [DockerFact]
    public async Task An_account_that_is_switched_off_loses_its_open_panel_connection_and_cannot_open_another()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var (token, account) = await ScopedAdminAsync(KioskScope.Everything(), Permissions.KiosksView);

        await using var open = await RawHubConnection.ConnectAsync(_fixture, "admin", token);

        var off = await admin.PostAsJsonAsync($"/api/admin/admins/{account.Id}/active", new { isActive = false, reason = "Left" });
        off.IsSuccessStatusCode.Should().BeTrue(await off.Content.ReadAsStringAsync());

        (await open.ClosedByServerAsync(Wait)).Should().BeTrue("a switched-off account hears nothing more");

        using var client = _fixture.CreateApiClient();
        var negotiate = await client.PostAsync(
            $"/hubs/admin/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);

        negotiate.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>The kiosk a message is about, when it is about one.</summary>
    private static Guid? KioskOf(JsonElement argument)
        => argument.ValueKind == JsonValueKind.Object
           && argument.TryGetProperty("kioskId", out var kiosk)
           && kiosk.ValueKind == JsonValueKind.String
            ? kiosk.GetGuid()
            : null;

    /// <summary>The next status the panel is told this kiosk has.</summary>
    private static async Task<string?> StatusOfAsync(RawHubConnection panel, Guid kioskId)
    {
        var heard = await panel.ReadUntilAsync(
            (target, argument) => target == "KioskStatusChanged" && KioskOf(argument) == kioskId, Wait);

        return heard[^1].Argument.GetProperty("status").GetString();
    }

    private static async Task BeatAsync(TestKiosk kiosk)
    {
        var response = await kiosk.Client.PostAsJsonAsync("/api/kiosks/heartbeat", new { appVersion = "0.7.0", printerOnline = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The kiosk has not called in for five minutes and the monitor notices:
    /// the time is moved in the database rather than slept through.
    /// </summary>
    private async Task GoSilentAsync(TestKiosk kiosk)
    {
        using var scope = _fixture.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId),
            Builders<Kiosk>.Update.Set(k => k.LastHeartbeatAt, DateTime.UtcNow.AddMinutes(-5)));

        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new MarkKioskOfflineCommand(kiosk.KioskId)))
            .Should().BeTrue("the kiosk has been silent for five minutes");
    }

    private async Task<string> SuperAdminTokenAsync()
    {
        using var client = await TestKiosks.SuperAdminAsync(_fixture);

        return client.DefaultRequestHeaders.Authorization!.Parameter!;
    }

    private const string AdminPassword = "LivePanelPassword!2026";

    /// <summary>An account with one new role of exactly these permissions and this kiosk scope, signed in.</summary>
    private async Task<(string Token, AdminUser Account)> ScopedAdminAsync(KioskScope kioskScope, params string[] permissions)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Live role {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var scope = _fixture.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

        var account = new AdminUser
        {
            Email = $"live.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Live operator",
            RoleIds = [(await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()],
            KioskScope = kioskScope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, AdminPassword);

        await scope.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        using var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = AdminPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        return ((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!, account);
    }
}
