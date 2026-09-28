using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Printers and printer commands: plan section 7.7, and the step 3 flow
/// 9.0 "a new kiosk's printers show in the panel, one is chosen, a test page
/// prints, the kiosk goes Online" (docs/TASKS.md T-064).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskPrinterTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskPrinterTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static object Printer(string name, bool color = true, bool duplex = true, bool a3 = false, bool online = true)
        => new { name, driverName = "Driver", isOnline = online, color, duplex, a3 };

    [DockerFact]
    public async Task A_new_kiosk_is_configured_from_the_panel_and_goes_online_after_a_test_page()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"printers-{Guid.NewGuid():N}"[..24], selectPrinter: false);

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("status").GetString()
            .Should().Be("NotConfigured");

        // The kiosk reports what it found.
        (await kiosk.Client.PutAsJsonAsync("/api/kiosks/me/printers", new
        {
            printers = new[] { Printer("HP LaserJet", color: false), Printer("Microsoft Print to PDF", a3: true) },
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var printers = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/printers");
        printers.GetProperty("printers").GetArrayLength().Should().Be(2);

        // "Default et".
        var select = await admin.PutAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/printer", new { name = "Microsoft Print to PDF" });
        select.StatusCode.Should().Be(HttpStatusCode.OK, await select.Content.ReadAsStringAsync());
        (await select.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effective").GetProperty("a3").GetBoolean().Should().BeTrue();

        // Choosing is not enough: the kiosk goes into service after a good test page (plan 7.7).
        var config = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");
        config.GetProperty("status").GetString().Should().Be("NotConfigured");
        config.GetProperty("selectedPrinter").GetString().Should().Be("Microsoft Print to PDF");

        var test = await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/test-print", null);
        test.StatusCode.Should().Be(HttpStatusCode.OK, await test.Content.ReadAsStringAsync());
        var commandId = (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A kiosk that was not listening reads it when it is back.
        var open = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands");
        open.EnumerateArray().Should().ContainSingle(command => command.GetProperty("id").GetGuid() == commandId
                                                                && command.GetProperty("type").GetString() == "TestPrint");

        var result = await kiosk.Client.PostAsJsonAsync(
            $"/api/kiosks/me/commands/{commandId}/result", new { succeeded = true, messageKey = "kioskCommands.testPagePrinted", sheetsUsed = 1 });
        result.StatusCode.Should().Be(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString().Should().Be("Test səhifəsi çap olundu.");

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("status").GetString()
            .Should().Be("Online");

        // The same answer again: nothing more happens.
        (await kiosk.Client.PostAsJsonAsync(
                $"/api/kiosks/me/commands/{commandId}/result", new { succeeded = false, messageKey = "printer.error", sheetsUsed = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var commands = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/commands");
        commands[0].GetProperty("status").GetString().Should().Be("Succeeded");
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands")).GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task A_capability_can_be_switched_off_but_never_on()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"caps-{Guid.NewGuid():N}"[..20], color: true, duplex: true, a3: false);

        var on = await admin.PutAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/capabilities", new { a3 = true });
        on.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("printers.cannotEnable");

        var off = await admin.PutAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/capabilities", new { color = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());

        var effective = (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("effectiveCapabilities");
        effective.GetProperty("color").GetBoolean().Should().BeFalse("the admin switched it off");
        effective.GetProperty("duplex").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task A_printer_the_kiosk_did_not_report_cannot_be_chosen()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"unknown-{Guid.NewGuid():N}"[..22]);

        var select = await admin.PutAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/printer", new { name = "Nowhere" });
        select.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task The_kiosk_hears_a_command_and_a_printer_change_over_its_hub()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"cmdhub-{Guid.NewGuid():N}"[..22]);
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);

        (await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/printers/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var command = await hub.WaitForAsync("KioskCommand", TimeSpan.FromSeconds(10));
        command.GetProperty("type").GetString().Should().Be("RefreshPrinters");

        (await admin.PutAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/capabilities", new { duplex = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await hub.WaitForAsync("PrinterConfigChanged", TimeSpan.FromSeconds(10)))
            .GetProperty("kioskId").GetGuid().Should().Be(kiosk.KioskId);
    }

    [DockerFact]
    public async Task A_command_older_than_a_day_expires_instead_of_running()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"expire-{Guid.NewGuid():N}"[..22]);

        var sent = await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/printers/refresh", null);
        var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().KioskCommands.UpdateOneAsync(
                Builders<KioskCommand>.Filter.Eq(command => command.Id, id),
                Builders<KioskCommand>.Update.Set(command => command.CreatedAt, DateTime.UtcNow.AddHours(-25)));
        }

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands")).GetArrayLength().Should().Be(0);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/commands"))[0]
            .GetProperty("status").GetString().Should().Be("Expired");
    }

    [DockerFact]
    public async Task The_configuration_carries_the_prices_and_the_formats_for_the_options_screen()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prices-{Guid.NewGuid():N}"[..22]);
        var config = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");

        config.GetProperty("prices").GetProperty("bwPerSide").GetDecimal().Should().Be(0.10m);
        config.GetProperty("prices").GetProperty("paperMultipliers").GetProperty("A3").GetDecimal().Should().Be(2m);
        config.GetProperty("paperBlockWhenInsufficient").GetBoolean().Should().BeTrue();
        config.GetProperty("idleResetSeconds").GetInt32().Should().Be(90);
    }
}
