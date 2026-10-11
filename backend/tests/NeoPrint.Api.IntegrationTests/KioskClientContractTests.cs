extern alias kiosk;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using kiosk::NeoPrint.Kiosk.Core;
using kiosk::NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The kiosk application's own clients against the real API: the requests
/// and hub messages it depends on keep their shape (plan sections 6, 7.4,
/// 7.11; docs/TASKS.md T-062, T-065).
/// </summary>
/// <remarks>
/// The other tests speak to the API the way a kiosk does; these use the
/// kiosk's code itself, so a renamed field or a changed hub message breaks
/// here rather than on a kiosk in the field.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class KioskClientContractTests
{
    private static readonly TimeSpan HubWait = TimeSpan.FromSeconds(15);

    private readonly NeoPrintApiFixture _fixture;

    public KioskClientContractTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private KioskApiClient Client(FakeAppPaths paths)
    {
        var http = new HttpClient(_fixture.Server.CreateHandler()) { BaseAddress = _fixture.Server.BaseAddress };
        return new KioskApiClient(http, new FakeDeviceKeyStore(paths), NullLogger<KioskApiClient>.Instance);
    }

    private IOptions<KioskSettings> Settings() => Options.Create(new KioskSettings { ApiBaseUrl = _fixture.Server.BaseAddress.ToString() });

    private static Task<DeviceRegistrationResponse> RegisterAsync(KioskApiClient api, string hostname)
        => api.RegisterAsync(
            new MachineFingerprint(hostname, "Linux", "6.1", Guid.NewGuid().ToString("n"), ["aa:bb:cc:dd:ee:01"], "Test CPU", 8, "1920x1080"),
            "Linux",
            "0.3.0",
            enrollmentToken: null,
            printers: [new DetectedPrinter("Test printer", "Test driver", IsOnline: true, SupportsColor: true, SupportsDuplex: true, SupportsA3: false)]);

    private async Task<Guid> ActivateAsync(Guid deviceId, string name)
    {
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = new
            {
                name,
                lat = 40.3793,
                lng = 49.8486,
                address = "Bakı",
                defaultLanguage = "az",
                paperCapacityA4 = 500,
                paperCapacityA3 = 250,
                adminPin = "135790",
            },
            selectedPrinterName = "Test printer",
        });

        activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());
        return (await activate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kioskId").GetGuid();
    }

    /// <summary>Puts paper in the tray, as PrintJobTests do until the refill endpoint arrives (T-067).</summary>
    private async Task LoadPaperAsync(Guid kioskId)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        await context.Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(kiosk => kiosk.Id, kioskId),
            Builders<Kiosk>.Update.Set("PaperStock.A4.Current", 500));
    }

    [DockerFact]
    public async Task A_waiting_kiosk_hears_its_activation_on_the_provisioning_hub()
    {
        if (Skip) { return; }

        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-provisioning");
        registration.Activated.Should().BeFalse();

        await using var hub = new ProvisioningHubClient(api, Settings(), NullLogger<ProvisioningHubClient>.Instance)
        {
            HandlerFactory = _fixture.Server.CreateHandler,
        };

        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.DeviceActivated += () =>
        {
            activated.TrySetResult();
            return Task.CompletedTask;
        };

        await hub.StartAsync(CancellationToken.None);
        hub.IsConnected.Should().BeTrue("a registered device may listen before it is activated");

        await ActivateAsync(registration.DeviceId, "Contract provisioning");

        await activated.Task.WaitAsync(HubWait);
    }

    [DockerFact]
    public async Task An_order_made_by_the_kiosk_client_is_queued_heard_on_the_kiosk_hub_printed_and_reported()
    {
        if (Skip) { return; }

        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-order");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract order");
        await LoadPaperAsync(kioskId);

        var configuration = await api.GetConfigurationAsync();
        configuration.KioskId.Should().Be(kioskId);
        configuration.SelectedPrinter.Should().Be("Test printer");
        configuration.Prices.Should().NotBeNull("the kiosk quotes from the prices in its configuration");

        await using var hub = new KioskHubClient(api, Settings(), NullLogger<KioskHubClient>.Instance)
        {
            HandlerFactory = _fixture.Server.CreateHandler,
        };

        var heard = new TaskCompletionSource<IReadOnlyList<QueueItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.QueueUpdated += items =>
        {
            if (items.Count > 0)
            {
                heard.TrySetResult(items);
            }

            return Task.CompletedTask;
        };

        await hub.StartAsync(CancellationToken.None);

        var options = new JobOptions(Copies: 2, PageRange: "1-3", Duplex: true);
        var job = await api.CreatePrintJobAsync("hesabat.pdf", 5, options, skipPayment: true, "contract-order-1");

        // The same key again is the same job (a lost answer retried).
        var again = await api.CreatePrintJobAsync("hesabat.pdf", 5, options, skipPayment: true, "contract-order-1");
        again.Id.Should().Be(job.Id);

        job.Status.Should().Be("Queued");
        job.SelectedPages.Should().Be(3);
        job.RequiredSheets.Should().Be(4, "two copies of three pages on both sides take two sheets each");
        job.Price.Total.Should().BeGreaterThan(0);

        (await heard.Task.WaitAsync(HubWait)).Should().Contain(item => item.JobId == job.Id);
        (await api.GetQueueAsync()).Should().ContainSingle(item => item.JobId == job.Id).Which.Position.Should().Be(1);

        var started = DateTime.UtcNow;
        var attempt = new PrintAttemptReport(1, "Test printer", "Test_printer-17", started, null, 1000, null, "sheets", Recovered: false);

        await api.ReportPrintJobStatusAsync(job.Id, "Printing", 0, 0, null, attempt: attempt);
        await api.ReportPrintJobStatusAsync(
            job.Id, "Completed", 6, 4, null, attempt: attempt with { FinishedAt = started.AddMinutes(1), CounterAfter = 1004 });

        // The same report again (a lost answer): one row, not two.
        await api.ReportPrintJobStatusAsync(
            job.Id, "Completed", 6, 4, null, attempt: attempt with { FinishedAt = started.AddMinutes(1), CounterAfter = 1004 });

        var done = await api.GetPrintJobAsync(job.Id);
        done.Status.Should().Be("Completed");
        done.PrintedSheets.Should().Be(4);
        (await api.GetQueueAsync()).Should().NotContain(item => item.JobId == job.Id);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs/{job.Id}");
        var attempts = detail.GetProperty("attempts");

        attempts.GetArrayLength().Should().Be(1);
        attempts[0].GetProperty("spoolerJobId").GetString().Should().Be("Test_printer-17");
        attempts[0].GetProperty("counterBefore").GetInt64().Should().Be(1000);
        attempts[0].GetProperty("counterAfter").GetInt64().Should().Be(1004);
        attempts[0].GetProperty("status").GetString().Should().Be("Completed");
    }

    [PdfiumFact]
    public async Task A_session_and_its_paid_app_order_reach_the_kiosk_client_which_downloads_the_pdf()
    {
        if (Skip) { return; }

        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-session");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract session");
        await LoadPaperAsync(kioskId);
        var code = (await api.GetConfigurationAsync()).Code;

        await using var hub = new KioskHubClient(api, Settings(), NullLogger<KioskHubClient>.Instance)
        {
            HandlerFactory = _fixture.Server.CreateHandler,
        };

        var opened = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.SessionOpened += (session, _) => { opened.TrySetResult(session); return Task.CompletedTask; };
        hub.SessionClosed += (_, reason) => { closed.TrySetResult(reason); return Task.CompletedTask; };
        await hub.StartAsync(CancellationToken.None);

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 2);
        var session = await (await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = code })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        (await opened.Task.WaitAsync(HubWait)).Should().Be(sessionId);

        using var place = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/print-jobs")
        {
            Content = JsonContent.Create(new { fileId, options = new { copies = 1, color = false, duplex = false, paper = "A4", pagesPerSheet = 1 } }),
        };
        place.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var order = await (await user.Client.SendAsync(place)).Content.ReadFromJsonAsync<JsonElement>();

        using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{order.GetProperty("paymentId").GetGuid()}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Card", useBonus = false, expectedAmount = order.GetProperty("amount").GetDecimal() }),
        };
        pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(pay)).StatusCode.Should().Be(HttpStatusCode.OK);

        // What the kiosk's worker sees and fetches (AppOrders): its queue, the
        // order's options, and the PDF it was priced on.
        var jobId = order.GetProperty("jobId").GetGuid();
        (await api.GetQueueAsync()).Should().Contain(item => item.JobId == jobId && item.Status == "Queued");

        var job = await api.GetPrintJobAsync(jobId);
        job.Source.Should().Be("MobileSession");
        job.PageCount.Should().Be(2);
        job.Options.Paper.Should().Be("A4");

        using var downloaded = new MemoryStream();
        await api.DownloadPrintFileAsync(jobId, downloaded);
        downloaded.ToArray().Should().Equal(await user.Client.GetByteArrayAsync($"/api/files/{fileId}/pdf"));

        (await user.Client.DeleteAsync($"/api/sessions/{sessionId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await closed.Task.WaitAsync(HubWait)).Should().Be("Closed");
    }

    [DockerFact]
    public async Task A_kiosk_in_safe_mode_is_taken_out_of_service_and_back_by_its_heartbeat()
    {
        if (Skip) { return; }

        var api = Client(new FakeAppPaths());
        var registration = await RegisterAsync(api, "contract-safe-mode");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract safe mode");
        await LoadPaperAsync(kioskId);

        var runtime = new KioskRuntime(api, new SystemClock(), NullLogger<KioskRuntime>.Instance) { SafeMode = true };
        await runtime.LoadConfigurationAsync();

        (await runtime.BeatAsync(printerOnline: true)).Should().BeTrue();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}");
        detail.GetProperty("status").GetString().Should().Be("Maintenance");
        detail.GetProperty("safeModeSince").ValueKind.Should().Be(JsonValueKind.String);

        // The dashboard reads the list: safe mode and the paper level are on each row.
        var row = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/kiosks"))
            .EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == kioskId);
        row.GetProperty("safeModeSince").ValueKind.Should().Be(JsonValueKind.String);
        row.GetProperty("paperA4Capacity").GetInt32().Should().BeGreaterThan(0);
        row.GetProperty("paperA4Current").GetInt32().Should().BeGreaterThan(0);

        var refused = () => api.CreatePrintJobAsync("hesabat.pdf", 1, new JobOptions(), skipPayment: true, "contract-safe-1");
        (await refused.Should().ThrowAsync<KioskApiException>()).Which.Code.Should().Be("printJobs.kioskNotTakingOrders");

        runtime.SafeMode = false;
        await runtime.BeatAsync(printerOnline: true);

        detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}");
        detail.GetProperty("status").GetString().Should().Be("Online");
        (await api.CreatePrintJobAsync("hesabat.pdf", 1, new JobOptions(), skipPayment: true, "contract-safe-2")).Status.Should().Be("Queued");
    }

    [DockerFact]
    public async Task Analytics_from_the_kiosk_queue_are_stored_once_and_only_what_the_catalogue_allows()
    {
        if (Skip) { return; }

        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-analytics");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract analytics");

        var queue = new AnalyticsQueue(paths, new SystemClock());
        queue.NewSession();
        queue.Track("file_selected", new Dictionary<string, string?> { ["format"] = "pdf", ["pages"] = "3", ["fileName"] = "Aliyev_passport.pdf" });
        queue.Track("screen_view", new Dictionary<string, string?> { ["screen"] = "Options" });
        queue.Track("keystroke_logger", new Dictionary<string, string?> { ["text"] = "secret" });

        // The first answer is lost: the same batch goes again under its key.
        string? sentKey = null;
        IReadOnlyList<AnalyticsRecord>? sentEvents = null;
        await queue.FlushAsync(
            async (key, events, cancellationToken) =>
            {
                sentKey = key;
                sentEvents = events;
                await api.SendAnalyticsAsync(key, events, cancellationToken);
            },
            CancellationToken.None);
        await api.SendAnalyticsAsync(sentKey!, sentEvents!);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var stored = await context.AnalyticsEvents
            .Find(Builders<NeoPrint.Domain.Analytics.AnalyticsEvent>.Filter.Eq(stored => stored.KioskId, kioskId))
            .ToListAsync();

        stored.Select(stored => stored.Event).Should().BeEquivalentTo(["file_selected", "screen_view"], "an unknown event is dropped, a batch sent twice is stored once");
        stored.Single(stored => stored.Event == "file_selected").Props.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["format"] = "pdf", ["pages"] = "3" }, "a property the catalogue does not list never reaches the database");

        // A time-series collection that expires by itself (plan section 2.17).
        var options = (await (await context.Database.ListCollectionsAsync(
                new ListCollectionsOptions { Filter = new MongoDB.Bson.BsonDocument("name", "analytics_events") })).SingleAsync())["options"]
            .AsBsonDocument;
        options.Contains("timeseries").Should().BeTrue();
        options["expireAfterSeconds"].ToInt64().Should().Be((long)TimeSpan.FromDays(180).TotalSeconds);
    }

    [DockerFact]
    public async Task The_cancel_screen_finds_an_order_by_its_code_and_cancels_it()
    {
        if (Skip) { return; }

        var api = Client(new FakeAppPaths());
        var registration = await RegisterAsync(api, "contract-cancel");
        await LoadPaperAsync(await ActivateAsync(registration.DeviceId, "Contract cancel"));

        var job = await api.CreatePrintJobAsync("qeyd.txt", 1, new JobOptions(), skipPayment: true, "contract-cancel-1");

        var found = await api.FindPrintJobByCodeAsync(job.JobCode);
        found.Id.Should().Be(job.Id);

        var cancelled = await api.CancelPrintJobAsync(job.Id);
        cancelled.Status.Should().Be("Cancelled");

        var unknown = () => api.FindPrintJobByCodeAsync("000000");
        (await unknown.Should().ThrowAsync<KioskApiException>()).Which.Status.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task What_the_kiosk_read_from_a_printer_is_what_the_printer_detail_shows()
    {
        if (Skip) { return; }

        // The kiosk's own PrinterDetails and client on one side, the admin API
        // on the other: every field has to arrive under the name the server reads.
        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-printer");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract printer");

        var details = new PrinterDetails(
            "Test printer",
            Manufacturer: "KYOCERA",
            Model: "ECOSYS P3145dn",
            SerialNumber: "R4P0123456",
            Firmware: "2S0_2000.004.012",
            DriverName: "Kyocera Classic Universaldriver PCL6",
            DriverVersion: "8.2.1",
            Connection: "Network",
            Address: "10.0.0.9",
            Uri: "ipp://10.0.0.9/ipp/print",
            State: "stopped",
            StateReasons: ["media-empty-error"],
            Online: true,
            LastError: "Cassette 1 is empty",
            Supplies: [new PrinterSupply("TK-3160", "toner", "black", 20), new PrinterSupply("Waste Toner Box", "waste", null, null)],
            Trays: [new PrinterTray("Cassette 1", "iso-a4", 0)],
            LifetimePages: 152_340,
            Features: new PrinterFeatures(Color: false, Duplex: true, PaperSizes: ["A4", "A5"], Trays: ["tray-1"], ResolutionDpi: 1200, NativeFormats: ["application/pdf"]),
            SpoolerJobs: [new SpoolerJob("Test_printer-7", 2048, "pending", DateTime.UtcNow.AddSeconds(-30))]);

        await api.SendPrinterTelemetryAsync([details]);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var printers = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}/printers/telemetry");
        var sample = printers[0].GetProperty("sample");

        sample.GetProperty("selected").GetBoolean().Should().BeTrue();
        sample.GetProperty("manufacturer").GetString().Should().Be("KYOCERA");
        sample.GetProperty("model").GetString().Should().Be("ECOSYS P3145dn");
        sample.GetProperty("serialNumber").GetString().Should().Be("R4P0123456");
        sample.GetProperty("firmware").GetString().Should().Be("2S0_2000.004.012");
        sample.GetProperty("driverVersion").GetString().Should().Be("8.2.1");
        sample.GetProperty("connection").GetString().Should().Be("Network");
        sample.GetProperty("address").GetString().Should().Be("10.0.0.9");
        sample.GetProperty("state").GetString().Should().Be("stopped");
        sample.GetProperty("stateReasons")[0].GetString().Should().Be("media-empty-error");
        sample.GetProperty("lastError").GetString().Should().Be("Cassette 1 is empty");
        sample.GetProperty("supplies")[0].GetProperty("levelPercent").GetInt32().Should().Be(20);
        sample.GetProperty("supplies")[0].GetProperty("color").GetString().Should().Be("black");
        sample.GetProperty("supplies")[1].GetProperty("type").GetString().Should().Be("waste");
        sample.GetProperty("trays")[0].GetProperty("levelPercent").GetInt32().Should().Be(0);
        sample.GetProperty("lifetimePages").GetInt64().Should().Be(152_340);
        sample.GetProperty("features").GetProperty("resolutionDpi").GetInt32().Should().Be(1200);
        sample.GetProperty("features").GetProperty("paperSizes")[1].GetString().Should().Be("A5");
        sample.GetProperty("spoolerJobs")[0].GetProperty("id").GetString().Should().Be("Test_printer-7");
        sample.GetProperty("spoolerJobs")[0].GetProperty("sizeBytes").GetInt64().Should().Be(2048);
    }

    [DockerFact]
    public async Task A_report_made_by_the_kiosk_collector_is_what_the_panel_reads()
    {
        if (Skip) { return; }

        // The kiosk's own collector and client on one side, the admin API on
        // the other: every group has to arrive under the name the server reads.
        var paths = new FakeAppPaths();
        var api = Client(paths);
        var registration = await RegisterAsync(api, "contract-telemetry");
        var kioskId = await ActivateAsync(registration.DeviceId, "Contract telemetry");

        var clock = new SystemClock();
        var runtime = new KioskRuntime(api, clock, NullLogger<KioskRuntime>.Instance);
        var configuration = await runtime.LoadConfigurationAsync();
        configuration.TelemetrySeconds.Should().Be(30, "the interval reaches the kiosk with its configuration");
        runtime.TelemetryInterval.Should().Be(TimeSpan.FromSeconds(30));

        (await runtime.BeatAsync(printerOnline: true)).Should().BeTrue();
        runtime.LastBackendLatencyMs.Should().NotBeNull("the heartbeat is how the kiosk measures the backend");

        var counters = new TelemetryCounters(clock);
        counters.JobFinished(6);
        counters.Printing("551203");

        var collector = new KioskTelemetryCollector(new FakeSystemMetrics(), runtime, counters, clock);

        var report = await collector.CollectAsync(new TelemetrySources
        {
            Screen = () => new TelemetryScreenState("Home", "Mobile", QueueLength: 1, ScreenWidth: 1920, ScreenHeight: 1080),
            Hub = () => new HubState(Connected: true, LastReconnectAt: null, DisconnectedSince: null),
            FlashInserted = _ => Task.FromResult<bool?>(false),
            SpoolFiles = () => 4,
            FailedFlashUploads = () => 0,
            PendingCommands = () => 0,
            LibreOffice = () => true,
            Ghostscript = () => true,
        });

        await api.SendTelemetryAsync(report);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}/telemetry");
        var sample = view.GetProperty("sample");

        view.GetProperty("stale").GetBoolean().Should().BeFalse();
        sample.GetProperty("app").GetProperty("screen").GetString().Should().Be("Home");
        sample.GetProperty("app").GetProperty("session").GetString().Should().Be("Mobile");
        sample.GetProperty("app").GetProperty("version").GetString().Should().Be(KioskRuntime.AppVersion);
        sample.GetProperty("system").GetProperty("hostname").GetString().Should().Be("fake-kiosk");
        sample.GetProperty("system").GetProperty("cpuPercent").GetDouble().Should().Be(7.5);
        sample.GetProperty("system").GetProperty("memoryUsedPercent").GetDouble().Should().Be(25);
        sample.GetProperty("system").GetProperty("spoolDiskFreeBytes").GetInt64().Should().Be(50L * 1024 * 1024 * 1024);
        sample.GetProperty("network").GetProperty("connectionType").GetString().Should().Be("Ethernet");
        sample.GetProperty("network").GetProperty("localIp").GetString().Should().Be("192.168.0.10");
        sample.GetProperty("network").GetProperty("hubConnected").GetBoolean().Should().BeTrue();
        sample.GetProperty("network").GetProperty("backendLatencyMs").GetInt32().Should().Be(runtime.LastBackendLatencyMs);
        sample.GetProperty("peripherals").GetProperty("flashInserted").GetBoolean().Should().BeFalse();
        sample.GetProperty("peripherals").GetProperty("screenWidth").GetInt32().Should().Be(1920);
        sample.GetProperty("work").GetProperty("queueLength").GetInt32().Should().Be(1);
        sample.GetProperty("work").GetProperty("currentJob").GetString().Should().Be("551203");
        sample.GetProperty("work").GetProperty("jobsToday").GetInt32().Should().Be(1);
        sample.GetProperty("work").GetProperty("sheetsToday").GetInt32().Should().Be(6);
        sample.GetProperty("work").GetProperty("spoolFiles").GetInt32().Should().Be(4);
        sample.GetProperty("services").GetProperty("libreOffice").GetBoolean().Should().BeTrue();
        sample.GetProperty("services").GetProperty("configurationAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
