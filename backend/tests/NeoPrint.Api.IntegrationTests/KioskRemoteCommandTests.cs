using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
