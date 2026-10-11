using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using NeoPrint.Application.Kiosks;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Kiosk telemetry (plan section 7.9): the kiosk's report, the snapshot and
/// the graphs the panel reads, and the limits that open incidents.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskTelemetryTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly NeoPrintApiFixture _fixture;

    public KioskTelemetryTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static object Report(long spoolFreeBytes = 40 * Gb, double cpu = 12.5, int queue = 2) => new
    {
        sentAt = DateTime.UtcNow,
        app = new { version = "0.7.0", startedAt = DateTime.UtcNow.AddHours(-3), screen = "Home", session = "Mobile" },
        system = new
        {
            os = "Linux",
            osVersion = "Ubuntu 24.04",
            hostname = "kiosk-test",
            cpuPercent = cpu,
            memoryUsedBytes = 3 * Gb,
            memoryTotalBytes = 8 * Gb,
            systemDiskFreeBytes = 60 * Gb,
            spoolDiskFreeBytes = spoolFreeBytes,
            uptimeSeconds = 86_400,
        },
        network = new { connectionType = "Ethernet", localIp = "192.168.1.20", backendLatencyMs = 45, hubConnected = true },
        peripherals = new { flashInserted = false, screenWidth = 1920, screenHeight = 1080 },
        work = new { queueLength = queue, jobsToday = 7, sheetsToday = 31, spoolFiles = 1, failedFlashUploads = 0, pendingCommands = 0 },
        services = new { libreOffice = true, ghostscript = true, featureFlags = "a1b2c3" },
    };

    private static async Task SendAsync(HttpClient kiosk, object report)
    {
        var response = await kiosk.PostAsJsonAsync("/api/kiosks/me/telemetry", report);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The server stores at most one sample per kiosk in this time.</summary>
    private static Task NextSampleAsync() => Task.Delay(KioskTelemetryHandlers.MinimumSpacing + TimeSpan.FromMilliseconds(300));

    private static async Task<List<JsonElement>> OpenIncidentsAsync(HttpClient admin, Guid kioskId, string type)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/incidents?status=Open&type={type}&pageSize=100");

        return [.. page.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("kioskId").GetGuid() == kioskId)];
    }

    [DockerFact]
    public async Task A_kiosk_report_becomes_the_snapshot_the_panel_reads()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"tele-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        // Before the first report: no sample, and that is said rather than shown as zeros.
        var empty = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/telemetry");
        empty.TryGetProperty("sample", out _).Should().BeFalse();
        empty.GetProperty("stale").GetBoolean().Should().BeTrue();

        await SendAsync(kiosk.Client, Report());

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/telemetry");
        var sample = view.GetProperty("sample");

        view.GetProperty("stale").GetBoolean().Should().BeFalse();
        view.GetProperty("intervalSeconds").GetInt32().Should().Be(30);

        sample.GetProperty("app").GetProperty("version").GetString().Should().Be("0.7.0");
        sample.GetProperty("app").GetProperty("session").GetString().Should().Be("Mobile");
        sample.GetProperty("system").GetProperty("hostname").GetString().Should().Be("kiosk-test");
        sample.GetProperty("system").GetProperty("cpuPercent").GetDouble().Should().Be(12.5);
        sample.GetProperty("system").GetProperty("memoryUsedPercent").GetDouble().Should().BeApproximately(37.5, 0.01);
        sample.GetProperty("system").GetProperty("clockOffsetMs").GetInt64().Should().BeInRange(-5000, 5000);
        sample.GetProperty("network").GetProperty("localIp").GetString().Should().Be("192.168.1.20");
        sample.GetProperty("work").GetProperty("queueLength").GetInt32().Should().Be(2);
        sample.GetProperty("services").GetProperty("libreOffice").GetBoolean().Should().BeTrue();

        // What the server knows itself is added next to what the kiosk said.
        var status = view.GetProperty("status");
        status.GetProperty("status").GetString().Should().Be("Online");
        status.GetProperty("paper").GetProperty("A4").GetProperty("capacity").GetInt32().Should().Be(500);
        status.GetProperty("openIncidents").GetInt64().Should().Be(0);

        // The interval reaches the kiosk with its configuration.
        var config = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");
        config.GetProperty("telemetrySeconds").GetInt32().Should().Be(30);
    }

    [DockerFact]
    public async Task The_series_is_read_back_as_averaged_points()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"tele-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        await SendAsync(kiosk.Client, Report(cpu: 10, queue: 1));
        await NextSampleAsync();
        await SendAsync(kiosk.Client, Report(cpu: 30, queue: 3));

        // One slice over the whole hour: the two samples averaged.
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-30).ToString("O"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(30).ToString("O"));
        var points = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/kiosks/{kiosk.KioskId}/telemetry/history?from={from}&to={to}&points=10");

        var total = points.EnumerateArray().Sum(point => point.GetProperty("samples").GetInt32());
        total.Should().Be(2);

        var weighted = points.EnumerateArray()
            .Sum(point => point.GetProperty("cpuPercent").GetDouble() * point.GetProperty("samples").GetInt32()) / total;

        weighted.Should().BeApproximately(20, 0.01);
        points.EnumerateArray().First().GetProperty("memoryUsedPercent").GetDouble().Should().BeApproximately(37.5, 0.01);

        // Reports that arrive faster than the series is worth are not stored.
        await SendAsync(kiosk.Client, Report(cpu: 99));
        var after = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/kiosks/{kiosk.KioskId}/telemetry/history?from={from}&to={to}&points=10");
        after.EnumerateArray().Sum(point => point.GetProperty("samples").GetInt32()).Should().Be(2);

        // A range the wrong way round is refused in words.
        var wrong = await admin.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/telemetry/history?from={to}&to={from}");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("telemetry.rangeInvalid");
    }

    [DockerFact]
    public async Task A_nearly_full_disk_opens_one_incident_and_closes_it_when_space_is_back()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"tele-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        await SendAsync(kiosk.Client, Report(spoolFreeBytes: Gb));

        var open = await OpenIncidentsAsync(admin, kiosk.KioskId, "DiskLow");
        open.Should().ContainSingle();
        open[0].GetProperty("severity").GetString().Should().Be("Medium");
        open[0].GetProperty("details").GetString().Should().Contain("1024 MB");

        // Still low on the next report: the same incident, not a second one.
        await NextSampleAsync();
        await SendAsync(kiosk.Client, Report(spoolFreeBytes: Gb / 2));
        (await OpenIncidentsAsync(admin, kiosk.KioskId, "DiskLow")).Should().ContainSingle();

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/telemetry");
        view.GetProperty("status").GetProperty("openIncidents").GetInt64().Should().Be(1);
        view.GetProperty("sample").GetProperty("breaches").TryGetProperty("disk", out _).Should().BeTrue();

        // Space is back: the incident closes by itself.
        await NextSampleAsync();
        await SendAsync(kiosk.Client, Report(spoolFreeBytes: 30 * Gb));
        (await OpenIncidentsAsync(admin, kiosk.KioskId, "DiskLow")).Should().BeEmpty();

        var resolved = await admin.GetFromJsonAsync<JsonElement>("/api/admin/incidents?status=Resolved&type=DiskLow&pageSize=100");
        resolved.GetProperty("items").EnumerateArray()
            .Should().Contain(item => item.GetProperty("kioskId").GetGuid() == kiosk.KioskId);
    }

    [DockerFact]
    public async Task Only_the_kiosk_reports_and_only_the_panel_reads()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"tele-{Guid.NewGuid():N}"[..20]);
        using var anonymous = _fixture.CreateApiClient();
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await anonymous.PostAsJsonAsync("/api/kiosks/me/telemetry", Report())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsJsonAsync("/api/kiosks/me/telemetry", Report())).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);

        (await kiosk.Client.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/telemetry")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/telemetry")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await admin.GetAsync($"/api/admin/kiosks/{Guid.NewGuid()}/telemetry")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An empty report is a report: every value is optional.
        (await kiosk.Client.PostAsJsonAsync("/api/kiosks/me/telemetry", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
