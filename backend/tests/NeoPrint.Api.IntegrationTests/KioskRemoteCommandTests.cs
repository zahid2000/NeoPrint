using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Remote commands (plan section 7.8): pausing and resuming a kiosk, and
/// restarting its application or the machine.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskRemoteCommandTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskRemoteCommandTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient kiosk)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = 1,
                options = new { copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = true,
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await kiosk.SendAsync(request);
    }

    /// <summary>A kiosk with paper in its tray: one that can take an order.</summary>
    private async Task<TestKiosk> ReadyKioskAsync(string name)
    {
        var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"{name}-{Guid.NewGuid():N}"[..22]);

        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(stored => stored.Id, kiosk.KioskId),
            Builders<Kiosk>.Update.Set("PaperStock.A4.Current", 500));

        return kiosk;
    }

    private static async Task<JsonElement> SendAsync(HttpClient admin, Guid kioskId, string command)
    {
        var response = await admin.PostAsync($"/api/admin/kiosks/{kioskId}/{command}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> StatusAsync(HttpClient kiosk)
        => (await kiosk.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("status").GetString();

    [DockerFact]
    public async Task A_paused_kiosk_takes_no_new_orders_at_once_and_takes_them_again_when_resumed()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync("pause");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);

        (await PlaceAsync(kiosk.Client)).StatusCode.Should().Be(HttpStatusCode.Created, "the kiosk is open");

        var pause = await SendAsync(admin, kiosk.KioskId, "pause");
        pause.GetProperty("type").GetString().Should().Be("Pause");
        pause.GetProperty("status").GetString().Should().Be("Sent");

        // The kiosk is told over its hub...
        (await hub.WaitForAsync("KioskCommand", TimeSpan.FromSeconds(10))).GetProperty("type").GetString().Should().Be("Pause");

        // ...but the backend does not wait for it: the status has changed and
        // an order is refused, before the kiosk has answered anything.
        (await StatusAsync(kiosk.Client)).Should().Be("Maintenance");
        (await PlaceAsync(kiosk.Client)).StatusCode.Should().NotBe(HttpStatusCode.Created);

        // A heartbeat does not undo a decision somebody made.
        (await kiosk.Client.PostAsJsonAsync("/api/kiosks/heartbeat", new { appVersion = "0.7.0", printerOnline = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusAsync(kiosk.Client)).Should().Be("Maintenance");

        // The kiosk answers; the panel sees the result in words.
        var paused = await kiosk.Client.PostAsJsonAsync(
            $"/api/kiosks/me/commands/{pause.GetProperty("id").GetGuid()}/result",
            new { succeeded = true, messageKey = "kioskCommands.paused", sheetsUsed = 0 });
        paused.StatusCode.Should().Be(HttpStatusCode.OK, await paused.Content.ReadAsStringAsync());

        var listed = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/commands"))[0];
        listed.GetProperty("status").GetString().Should().Be("Succeeded");
        listed.GetProperty("result").GetString().Should().Contain("yeni sifariş qəbul etmir");

        var resume = await SendAsync(admin, kiosk.KioskId, "resume");
        resume.GetProperty("type").GetString().Should().Be("Resume");

        (await StatusAsync(kiosk.Client)).Should().Be("Online");
        (await PlaceAsync(kiosk.Client)).StatusCode.Should().Be(HttpStatusCode.Created, "resumed");
    }

    [DockerFact]
    public async Task Pressing_a_command_twice_does_not_send_it_twice()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"twice-{Guid.NewGuid():N}"[..22]);

        var first = await SendAsync(admin, kiosk.KioskId, "restart-app");
        var again = await SendAsync(admin, kiosk.KioskId, "restart-app");

        again.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid(), "the first is still open");

        var open = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands");
        open.EnumerateArray().Count(command => command.GetProperty("type").GetString() == "RestartApp").Should().Be(1);

        // Another kind of command is another command.
        var reboot = await SendAsync(admin, kiosk.KioskId, "reboot");
        reboot.GetProperty("id").GetGuid().Should().NotBe(first.GetProperty("id").GetGuid());

        // Pausing twice is one pause.
        var pause = await SendAsync(admin, kiosk.KioskId, "pause");
        (await SendAsync(admin, kiosk.KioskId, "pause")).GetProperty("id").GetGuid().Should().Be(pause.GetProperty("id").GetGuid());
    }

    [DockerFact]
    public async Task A_restart_is_acknowledged_first_and_answered_by_the_application_that_comes_back()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"restart-{Guid.NewGuid():N}"[..22]);

        var id = (await SendAsync(admin, kiosk.KioskId, "restart-app")).GetProperty("id").GetGuid();

        // "I have it": the kiosk will restart when the printer is free.
        var acknowledged = await kiosk.Client.PostAsync($"/api/kiosks/me/commands/{id}/ack", null);
        acknowledged.StatusCode.Should().Be(HttpStatusCode.OK, await acknowledged.Content.ReadAsStringAsync());
        (await acknowledged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Acknowledged");

        // Said twice - a lost answer - it is still one acknowledgement.
        (await kiosk.Client.PostAsync($"/api/kiosks/me/commands/{id}/ack", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The application that starts afterwards finds the command still
        // open, and that is how it knows what it owes an answer for.
        var open = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands");
        var mine = open.EnumerateArray().Single(command => command.GetProperty("id").GetGuid() == id);
        mine.GetProperty("status").GetString().Should().Be("Acknowledged");

        (await kiosk.Client.PostAsJsonAsync(
            $"/api/kiosks/me/commands/{id}/result", new { succeeded = true, messageKey = "kioskCommands.restarted", sheetsUsed = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var done = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/commands"))
            .EnumerateArray().Single(command => command.GetProperty("id").GetGuid() == id);
        done.GetProperty("status").GetString().Should().Be("Succeeded");
        done.GetProperty("result").GetString().Should().Be("Kiosk tətbiqi yenidən başladı.");

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands")).GetArrayLength().Should().Be(0);

        // Another kiosk cannot acknowledge or answer this one's commands.
        using var other = await TestKiosks.ActivatedAsync(_fixture, $"other-{Guid.NewGuid():N}"[..22]);
        var theirs = (await SendAsync(admin, kiosk.KioskId, "reboot")).GetProperty("id").GetGuid();
        (await other.Client.PostAsync($"/api/kiosks/me/commands/{theirs}/ack", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Gives every request of a test host the same remote address: the test server has none.</summary>
    private sealed class FromAddress(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                return following(context);
            });

            next(app);
        };
    }

    [DockerFact]
    public async Task Every_remote_command_is_in_the_audit_log_who_which_kiosk_what_from_where_and_what_came_of_it()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"audit-{Guid.NewGuid():N}"[..22]);

        await using var host = _fixture.WithHttpOnlyHost(builder => builder.ConfigureTestServices(services =>
            services.AddTransient<IStartupFilter>(_ => new FromAddress("203.0.113.7"))));

        using var panel = host.CreateClient();
        panel.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
        using var device = host.CreateClient();
        device.DefaultRequestHeaders.Authorization = kiosk.Client.DefaultRequestHeaders.Authorization;

        var me = (await panel.GetFromJsonAsync<JsonElement>("/api/admin/me")).GetProperty("id").GetGuid();

        // Sent, pressed again, acknowledged by the kiosk, answered by the kiosk.
        var reboot = (await SendAsync(panel, kiosk.KioskId, "reboot")).GetProperty("id").GetGuid();
        await SendAsync(panel, kiosk.KioskId, "reboot");
        (await device.PostAsync($"/api/kiosks/me/commands/{reboot}/ack", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await device.PostAsJsonAsync(
            $"/api/kiosks/me/commands/{reboot}/result", new { succeeded = false, messageKey = "kioskCommands.rebootRefused", sheetsUsed = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var pause = (await SendAsync(panel, kiosk.KioskId, "pause")).GetProperty("id").GetGuid();
        (await device.PostAsJsonAsync(
            $"/api/kiosks/me/commands/{pause}/result", new { succeeded = true, messageKey = "kioskCommands.paused", sheetsUsed = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(row => row.EntityId == kiosk.KioskId.ToString() && row.Action.StartsWith("kiosks.command."))
            .SortBy(row => row.CreatedAt)
            .ToListAsync();

        rows.Select(row => row.Action).Should().Equal(
            "kiosks.command.Reboot",
            "kiosks.command.Reboot",
            "kiosks.command.Reboot.Acknowledged",
            "kiosks.command.Reboot.Failed",
            "kiosks.command.Pause",
            "kiosks.command.Pause.Succeeded");

        // Who, which kiosk, from where - on every row.
        rows.Should().AllSatisfy(row =>
        {
            row.AdminUserId.Should().Be(me, "the administrator who sent the command, also on the kiosk's answers");
            row.EntityType.Should().Be("Kiosk");
            row.Ip.Should().Be("203.0.113.7");
        });

        // The request and what came of it name the same command.
        rows[0].After.Should().Be(reboot.ToString());
        rows[0].ActorType.Should().Be(NeoPrint.Domain.Common.ActorType.Admin);

        // Pressed twice: asked for, and nothing new sent.
        rows[1].After.Should().Be(reboot.ToString());
        rows[1].Reason.Should().Be("kioskCommands.reason.alreadyOpen");

        rows[2].Before.Should().Be(reboot.ToString());
        rows[2].ActorType.Should().Be(NeoPrint.Domain.Common.ActorType.Kiosk);

        // The result, by its key: the log is read in any language.
        rows[3].Before.Should().Be(reboot.ToString());
        rows[3].After.Should().Be("kioskCommands.rebootRefused");
        rows[5].After.Should().Be("kioskCommands.paused");
    }

    [DockerFact]
    public async Task A_command_nobody_answered_is_written_down_as_expired()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"auditexp-{Guid.NewGuid():N}"[..22]);

        var id = (await SendAsync(admin, kiosk.KioskId, "restart-app")).GetProperty("id").GetGuid();

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        await context.KioskCommands.UpdateOneAsync(
            Builders<KioskCommand>.Filter.Eq(command => command.Id, id),
            Builders<KioskCommand>.Update.Set(command => command.CreatedAt, DateTime.UtcNow.AddHours(-25)));

        // The kiosk comes back a day later: the command is not handed to it.
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands")).GetArrayLength().Should().Be(0);

        var expired = await context.AuditLogs
            .Find(row => row.EntityId == kiosk.KioskId.ToString() && row.Action == "kiosks.command.RestartApp.Expired")
            .ToListAsync();

        expired.Should().ContainSingle().Which.Before.Should().Be(id.ToString());
        expired[0].ActorType.Should().Be(NeoPrint.Domain.Common.ActorType.System);
    }

    [DockerFact]
    public async Task A_kiosk_that_was_offline_when_paused_comes_back_paused()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync("offpause");

        using (var scope = _fixture.Services.CreateScope())
        {
            var kiosks = scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Abstractions.IKioskRepository>();
            var stored = (await kiosks.FindByIdAsync(kiosk.KioskId))!;
            stored.MoveTo(KioskStatus.Offline, NeoPrint.Domain.Common.ActorContext.System("test: heartbeat stopped"), DateTime.UtcNow);
            await kiosks.UpdateAsync(stored);
        }

        await SendAsync(admin, kiosk.KioskId, "pause");

        // Its heartbeat returns: it is reachable again, and still paused.
        (await kiosk.Client.PostAsJsonAsync("/api/kiosks/heartbeat", new { appVersion = "0.7.0", printerOnline = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await StatusAsync(kiosk.Client)).Should().Be("Maintenance");

        // The command waited for it.
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands"))
            .EnumerateArray().Select(command => command.GetProperty("type").GetString()).Should().Contain("Pause");

        await SendAsync(admin, kiosk.KioskId, "resume");
        (await StatusAsync(kiosk.Client)).Should().Be("Online");
    }
}
