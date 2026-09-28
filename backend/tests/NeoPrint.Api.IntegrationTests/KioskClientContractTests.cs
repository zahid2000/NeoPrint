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

        var refused = () => api.CreatePrintJobAsync("hesabat.pdf", 1, new JobOptions(), skipPayment: true, "contract-safe-1");
        (await refused.Should().ThrowAsync<KioskApiException>()).Which.Code.Should().Be("printJobs.kioskNotTakingOrders");

        runtime.SafeMode = false;
        await runtime.BeatAsync(printerOnline: true);

        detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}");
        detail.GetProperty("status").GetString().Should().Be("Online");
        (await api.CreatePrintJobAsync("hesabat.pdf", 1, new JobOptions(), skipPayment: true, "contract-safe-2")).Status.Should().Be("Queued");
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
}
