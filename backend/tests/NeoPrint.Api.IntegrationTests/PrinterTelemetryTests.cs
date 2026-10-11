using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Printer telemetry (plan section 7.10): what the kiosk relays from its
/// printers, the readings and history the panel reads, and the stuck-spooler
/// incident.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrinterTelemetryTests
{
    private const string Selected = "Test printer";

    private readonly NeoPrintApiFixture _fixture;

    public PrinterTelemetryTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static object Reading(
        string name = Selected, int black = 40, string state = "idle", string[]? reasons = null, object[]? jobs = null, int tray = 60,
        long pages = 48_213) => new
    {
        name,
        manufacturer = "HP",
        model = "Color LaserJet Pro M454dn",
        serialNumber = "VNB3R12345",
        firmware = "002_2306A",
        driverName = "HP Universal Printing PCL 6",
        connection = "Network",
        address = "10.0.0.5",
        uri = "ipp://10.0.0.5/ipp/print",
        state,
        stateReasons = reasons ?? [],
        online = true,
        supplies = new object[]
        {
            new { name = "Black Cartridge", type = "toner", color = "#000000", levelPercent = black },
            new { name = "Imaging Drum", type = "drum" },
        },
        trays = new[] { new { name = "Tray 1", media = "A4", levelPercent = tray } },
        lifetimePages = pages,
        features = new { color = true, duplex = true, paperSizes = new[] { "A4", "A5" }, resolutionDpi = 600, nativeFormats = new[] { "application/pdf" } },
        spoolerJobs = jobs ?? [],
    };

    private static async Task SendAsync(HttpClient kiosk, params object[] printers)
    {
        var response = await kiosk.PostAsJsonAsync("/api/kiosks/me/printers/telemetry", new { printers });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static string Query(string printer) => $"printer={Uri.EscapeDataString(printer)}";

    [DockerFact]
    public async Task What_the_printers_said_is_what_the_panel_reads_the_selected_one_first()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prn-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry"))
            .GetArrayLength().Should().Be(0, "nothing has been reported yet");

        await SendAsync(kiosk.Client, Reading("A spare printer", black: 90), Reading());

        var printers = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry");
        printers.GetArrayLength().Should().Be(2);

        var first = printers[0].GetProperty("sample");
        first.GetProperty("printer").GetProperty("printerName").GetString().Should().Be(Selected);
        first.GetProperty("selected").GetBoolean().Should().BeTrue();
        printers[0].GetProperty("stale").GetBoolean().Should().BeFalse();

        first.GetProperty("model").GetString().Should().Be("Color LaserJet Pro M454dn");
        first.GetProperty("serialNumber").GetString().Should().Be("VNB3R12345");
        first.GetProperty("connection").GetString().Should().Be("Network");
        first.GetProperty("state").GetString().Should().Be("idle");
        first.GetProperty("lifetimePages").GetInt64().Should().Be(48_213);
        first.GetProperty("features").GetProperty("duplex").GetBoolean().Should().BeTrue();

        var supplies = first.GetProperty("supplies");
        supplies[0].GetProperty("name").GetString().Should().Be("Black Cartridge");
        supplies[0].GetProperty("levelPercent").GetInt32().Should().Be(40);
        supplies[1].TryGetProperty("levelPercent", out _).Should().BeFalse("a drum that gives no level has none - not zero");

        printers[1].GetProperty("sample").GetProperty("selected").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task A_printer_history_shows_its_levels_and_what_changed()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prn-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        await SendAsync(kiosk.Client, Reading(black: 5, state: "stopped", reasons: ["toner-low"]));
        await Task.Delay(1100);
        await SendAsync(kiosk.Client, Reading(black: 100, state: "idle"));

        var events = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry/events?{Query(Selected)}");

        var kinds = events.EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        kinds.Should().BeEquivalentTo(["State", "Reasons", "SupplyReplaced"]);

        var replaced = events.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "SupplyReplaced");
        replaced.GetProperty("from").GetString().Should().Be("Black Cartridge: 5%");
        replaced.GetProperty("to").GetString().Should().Be("Black Cartridge: 100%");
        replaced.GetProperty("subject").GetString().Should().Be("Black Cartridge");
        replaced.GetProperty("fromLevel").GetInt32().Should().Be(5);
        replaced.GetProperty("toLevel").GetInt32().Should().Be(100);
        replaced.GetProperty("lifetimePages").GetInt64().Should().Be(48_213);

        var history = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry/history?{Query(Selected)}&points=10");

        history.GetArrayLength().Should().BeGreaterThan(0);
        var last = history[history.GetArrayLength() - 1];
        last.GetProperty("lifetimePages").GetInt64().Should().Be(48_213);
        last.GetProperty("supplyLevels").GetProperty("Black Cartridge").GetInt32().Should().Be(100);

        // Another printer's name finds nothing rather than this printer's readings.
        (await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry/events?{Query("Somebody else's")}")).GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task What_happens_to_a_printer_is_written_down_when_it_is_seen_and_outlives_the_readings()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prn-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        async Task<List<string>> KindsAsync()
        {
            var events = await admin.GetFromJsonAsync<JsonElement>(
                $"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry/events?{Query(Selected)}");

            return [.. events.EnumerateArray().Select(e => e.GetProperty("kind").GetString() ?? string.Empty)];
        }

        // The first reading of a printer is where its history starts: nothing happened yet.
        await SendAsync(kiosk.Client, Reading(tray: 8, pages: 100));
        (await KindsAsync()).Should().BeEmpty();

        // A jam, then paper put in while it is cleared, then the jam gone.
        await SendAsync(kiosk.Client, Reading(tray: 8, state: "stopped", reasons: ["media-jam-error"], pages: 101));
        await SendAsync(kiosk.Client, Reading(tray: 95, state: "stopped", reasons: ["media-jam-error"], pages: 101));
        await SendAsync(kiosk.Client, Reading(tray: 95, pages: 102));

        (await KindsAsync()).Should().BeEquivalentTo(
            ["State", "Reasons", "JamStarted", "PaperRefilled", "State", "Reasons", "JamEnded"]);

        // The rows are in a collection of their own, with the tray and the
        // page counter of the moment - and they stay when the readings they
        // were found in are gone.
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NeoPrint.Infrastructure.Persistence.MongoContext>();

        var stored = await context.PrinterEvents.Find(e => e.KioskId == kiosk.KioskId).ToListAsync();
        stored.Should().HaveCount(7);

        var refilled = stored.Single(e => e.Kind == "PaperRefilled");
        refilled.Subject.Should().Be("Tray 1");
        refilled.FromLevel.Should().Be(8);
        refilled.ToLevel.Should().Be(95);
        refilled.LifetimePages.Should().Be(101);
        stored.Single(e => e.Kind == "JamEnded").Subject.Should().Be("media-jam-error");

        await context.PrinterTelemetry.DeleteManyAsync(sample => sample.Printer.KioskId == kiosk.KioskId);
        (await KindsAsync()).Should().HaveCount(7);

        // Written twice, an event is there once.
        var store = scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Kiosks.IPrinterEventStore>();
        await store.AddManyAsync(stored);
        (await context.PrinterEvents.CountDocumentsAsync(e => e.KioskId == kiosk.KioskId)).Should().Be(7);

        // The collection expires by itself, a year after the event.
        var indexes = await (await context.PrinterEvents.WithoutSession.Indexes.ListAsync()).ToListAsync();
        indexes.Single(index => index["name"] == "ttl_printer_events")["expireAfterSeconds"].ToInt64()
            .Should().Be(365L * 24 * 60 * 60);
    }

    [DockerFact]
    public async Task A_job_stuck_in_the_system_spooler_opens_one_incident_and_closes_it_when_the_queue_moves()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prn-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        async Task<List<JsonElement>> OpenAsync()
        {
            var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/incidents?status=Open&type=SpoolerStuck&pageSize=100");
            return [.. page.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("kioskId").GetGuid() == kiosk.KioskId)];
        }

        var stuck = new object[] { new { id = "Test_printer-17", sizeBytes = 204_800, state = "processing", submittedAt = DateTime.UtcNow.AddMinutes(-9) } };

        await SendAsync(kiosk.Client, Reading(state: "stopped", reasons: ["media-jam"], jobs: stuck));

        var open = await OpenAsync();
        open.Should().ContainSingle();
        open[0].GetProperty("severity").GetString().Should().Be("High");
        open[0].GetProperty("details").GetString().Should().Contain("Test_printer-17").And.Contain(Selected);

        // Still there a minute later: the same incident.
        await SendAsync(kiosk.Client, Reading(state: "stopped", reasons: ["media-jam"], jobs: stuck));
        (await OpenAsync()).Should().ContainSingle();

        // A fresh job is not a stuck one, and the old one is gone: closed.
        await SendAsync(
            kiosk.Client,
            Reading(jobs: [new { id = "Test_printer-18", sizeBytes = 1024, state = "processing", submittedAt = DateTime.UtcNow.AddSeconds(-20) }]));

        (await OpenAsync()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Only_the_kiosk_reports_and_only_a_panel_user_with_the_printers_permission_reads()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"prn-{Guid.NewGuid():N}"[..20]);
        using var anonymous = _fixture.CreateApiClient();
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await anonymous.PostAsJsonAsync("/api/kiosks/me/printers/telemetry", new { printers = new[] { Reading() } })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await kiosk.Client.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await admin.GetAsync($"/api/admin/kiosks/{Guid.NewGuid()}/printers/telemetry")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // What a printer leaves out arrives as null, and an entry without a
        // name is dropped: neither refuses the whole report (found on the
        // real stack - the fake printer's first report came back 400).
        await SendAsync(kiosk.Client, new
        {
            name = "Sparse printer",
            stateReasons = (string[]?)null,
            supplies = new object?[] { new { name = (string?)null, levelPercent = 50 }, new { name = "Black", levelPercent = 70 } },
            trays = (object[]?)null,
            features = new { color = true, paperSizes = (string[]?)null, trays = (string[]?)null, nativeFormats = (string[]?)null },
            spoolerJobs = new object?[] { new { id = (string?)null } },
        });

        var sparse = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry"))[0].GetProperty("sample");
        sparse.GetProperty("supplies").GetArrayLength().Should().Be(1);
        sparse.GetProperty("supplies")[0].GetProperty("name").GetString().Should().Be("Black");
        sparse.GetProperty("spoolerJobs").GetArrayLength().Should().Be(0);
        sparse.GetProperty("features").GetProperty("trays").GetArrayLength().Should().Be(0);

        // Nothing to say is not an error, and a reading without a name is dropped.
        (await kiosk.Client.PostAsJsonAsync("/api/kiosks/me/printers/telemetry", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await SendAsync(kiosk.Client, new { name = "   ", state = "idle" });

        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/printers/telemetry")).GetArrayLength().Should().Be(1);
    }
}
