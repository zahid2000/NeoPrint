using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// What an activated kiosk does: hold its configuration, report in, and work
/// out what it may still do (plan sections 7.3, 7.9, 7.11).
/// </summary>
public sealed class KioskRuntimeTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = Start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// Answers the calls the runtime makes, scripted per path.
    /// </summary>
    /// <remarks>
    /// Plan section 11 rules out a mocking library, so this is written by hand.
    /// </remarks>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _config = new();
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _heartbeat = new();

        public int ConfigCalls { get; private set; }

        public int HeartbeatCalls { get; private set; }

        public Guid DeviceId { get; } = Guid.NewGuid();

        public void OnConfig(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _config.Enqueue(respond);

        public void OnHeartbeat(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _heartbeat.Enqueue(respond);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/register", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new
                {
                    deviceId = DeviceId,
                    activated = true,
                    pairingCode = (string?)null,
                    pairingCodeExpiresAt = (DateTime?)null,
                    kioskId = Guid.NewGuid(),
                    kioskCode = "KSK-0001",
                }));
            }

            if (path.EndsWith("/token", StringComparison.Ordinal) ||
                path.EndsWith("/challenge", StringComparison.Ordinal))
            {
                // The client signs a challenge before its first authenticated
                // call; these tests are about what happens afterwards.
                return Task.FromResult(Json(new
                {
                    accessToken = "kiosk-token",
                    expiresAt = Start.AddHours(1),
                    tokenType = "kiosk",
                    nonce = "nonce",
                }));
            }

            if (path.EndsWith("/config", StringComparison.Ordinal))
            {
                ConfigCalls++;
                return Task.FromResult(_config.Count > 0 ? _config.Dequeue()(request) : NotFound());
            }

            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                HeartbeatCalls++;
                return Task.FromResult(_heartbeat.Count > 0 ? _heartbeat.Dequeue()(request) : NotFound());
            }

            return Task.FromResult(NotFound());
        }

        public static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };

        public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

        public static HttpResponseMessage Unreachable()
            => throw new HttpRequestException("No connection");
    }

    private static object Configuration(
        string? printer = "HP LaserJet", int cacheTtlHours = 24, DateTime? changedAt = null) => new
    {
        kioskId = Guid.NewGuid(),
        code = "KSK-0001",
        name = "Mərkəz",
        status = printer is null ? "NotConfigured" : "Online",
        defaultLanguage = "az",
        selectedPrinter = printer,
        effectiveCapabilities = new { color = true, duplex = true, a3 = false },
        availablePrinters = Array.Empty<object>(),
        adminPinHash = "pbkdf2$hash",
        queueDisplayLimit = 5,
        maxFileSizeMb = 50,
        maxPages = 200,
        pricingProfileId = (Guid?)null,
        paperStock = new Dictionary<string, object> { ["A4"] = new { capacity = 500, current = 480 } },
        features = new Dictionary<string, bool> { ["scan.enabled"] = false },
        heartbeatSeconds = 15,
        cacheTtlHours,
        serverTime = changedAt ?? Start,
    };

    private static object Heartbeat(DateTime changedAt, string status = "Online") => new
    {
        status,
        nextInSeconds = 15,
        configurationChangedAt = changedAt,
        serverTime = Start,
    };

    private static async Task<(KioskRuntime Runtime, ScriptedHandler Handler, TestClock Clock)> CreateAsync()
    {
        var handler = new ScriptedHandler();
        var clock = new TestClock();

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://backend.test/") };

        var api = new KioskApiClient(
            http, new FakeDeviceKeyStore(new FakeAppPaths()), NullLogger<KioskApiClient>.Instance);

        // An activated kiosk has registered already; the client will not
        // authenticate until it knows its own device id.
        await api.RegisterAsync(
            new MachineFingerprint("test-host", "Linux", "6.1", "machine-1", [], "CPU", 8, "1920x1080"),
            "Linux",
            "0.1.0",
            enrollmentToken: null,
            printers: []);

        return (new KioskRuntime(api, clock, NullLogger<KioskRuntime>.Instance), handler, clock);
    }

    [Fact]
    public async Task Loading_the_configuration_keeps_it_with_the_time_it_arrived()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));

        var configuration = await runtime.LoadConfigurationAsync();

        configuration.Code.Should().Be("KSK-0001");
        configuration.SelectedPrinter.Should().Be("HP LaserJet");
        runtime.Configuration.Should().NotBeNull();
        runtime.IsBackendReachable.Should().BeTrue();

        // Fresh cache, backend reachable: everything works.
        runtime.Availability.Should().Be(KioskAvailability.Online);
    }

    [Fact]
    public async Task The_heartbeat_interval_comes_from_the_configuration()
    {
        var (runtime, handler, _) = await CreateAsync();

        // Before the first fetch there is still a sane default, so the loop
        // does not spin.
        runtime.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));

        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));
        await runtime.LoadConfigurationAsync();

        runtime.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task The_kiosk_knows_its_status_from_every_heartbeat_not_only_from_its_configuration()
    {
        // T-237: a status changes without the configuration changing - the
        // printer stops, safe mode ends - and the screen followed the
        // configuration's copy, which was as old as the last fetch.
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));
        await runtime.LoadConfigurationAsync();

        runtime.ServerStatus.Should().BeNull("nothing heard yet");

        var unchanged = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

        foreach (var status in new[] { "Maintenance", "Paused", "Online" })
        {
            handler.OnHeartbeat(_ => ScriptedHandler.Json(new
            {
                status,
                nextInSeconds = 15,
                configurationChangedAt = unchanged,
                serverTime = DateTime.UtcNow,
            }));

            (await runtime.BeatAsync(printerOnline: true)).Should().BeTrue();
            runtime.ServerStatus.Should().Be(status);
        }

        handler.ConfigCalls.Should().Be(1, "the configuration did not change, so it was not fetched again");

        // A heartbeat that does not get through leaves the last known word.
        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());
        await runtime.BeatAsync(printerOnline: true);
        runtime.ServerStatus.Should().Be("Online");
    }

    [Fact]
    public async Task A_failed_heartbeat_is_reported_rather_than_thrown()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));
        await runtime.LoadConfigurationAsync();

        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());

        var reached = await runtime.BeatAsync(printerOnline: true);

        // Losing the link is an expected state on a kiosk, not a crash
        // (docs/FAILURE_RECOVERY.md).
        reached.Should().BeFalse();
        runtime.IsBackendReachable.Should().BeFalse();
    }

    [Fact]
    public async Task A_detached_device_is_reported_as_revoked_not_as_a_lost_link()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(cacheTtlHours: 24)));
        await runtime.LoadConfigurationAsync();

        // "Detach device" in the panel. The device policy checks on every
        // request that the device is still active, so the heartbeat is refused.
        handler.OnHeartbeat(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        // Told apart from an unreachable backend on purpose: treated as a lost
        // link, the kiosk sits on the Degraded screen with the old kiosk code
        // in its footer and never returns to Activation, which is what plan
        // section 7.11 requires of it.
        await Assert.ThrowsAsync<DeviceRevokedException>(
            () => runtime.BeatAsync(printerOnline: true));

        runtime.Availability.Should().NotBe(
            KioskAvailability.Degraded,
            "a revoked device has no kiosk to be degraded about");
    }

    [Fact]
    public async Task A_configuration_refused_for_a_detached_device_is_revoked_too()
    {
        var (runtime, handler, _) = await CreateAsync();

        handler.OnConfig(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<DeviceRevokedException>(() => runtime.LoadConfigurationAsync());
    }

    [Fact]
    public async Task Losing_the_link_with_a_fresh_cache_is_degraded()
    {
        var (runtime, handler, clock) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(cacheTtlHours: 24)));
        await runtime.LoadConfigurationAsync();

        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());
        await runtime.BeatAsync(printerOnline: true);

        clock.Advance(TimeSpan.FromHours(1));

        runtime.Availability.Should().Be(KioskAvailability.Degraded);
        runtime.Availability.CanContinueQueue().Should().BeTrue();
        runtime.Availability.CanTakeNewOrders().Should().BeFalse();
    }

    [Fact]
    public async Task A_heartbeat_that_times_out_is_a_lost_link_not_the_kiosk_stopping()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(cacheTtlHours: 24)));
        await runtime.LoadConfigurationAsync();

        // What HttpClient throws when its own timeout runs out: a cancellation
        // nobody asked for. On the real stack this ended the kiosk's main loop.
        handler.OnHeartbeat(_ => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.", new TimeoutException()));

        (await runtime.BeatAsync(printerOnline: true)).Should().BeFalse();
        runtime.IsBackendReachable.Should().BeFalse();

        // The next beat gets through and the kiosk is back.
        handler.OnHeartbeat(_ => ScriptedHandler.Json(Heartbeat(Start)));
        (await runtime.BeatAsync(printerOnline: true)).Should().BeTrue();
        runtime.IsBackendReachable.Should().BeTrue();

        // Really stopping is still a cancellation.
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        handler.OnHeartbeat(_ => throw new OperationCanceledException(stopping.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.BeatAsync(printerOnline: true, stopping.Token));
    }

    [Fact]
    public async Task Losing_the_link_past_the_cache_window_is_offline()
    {
        var (runtime, handler, clock) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(cacheTtlHours: 24)));
        await runtime.LoadConfigurationAsync();

        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());
        await runtime.BeatAsync(printerOnline: true);

        clock.Advance(TimeSpan.FromHours(25));

        runtime.Availability.Should().Be(KioskAvailability.Offline);
        runtime.Availability.CanContinueQueue().Should().BeFalse();
    }

    [Fact]
    public async Task A_recovered_link_is_online_again()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));
        await runtime.LoadConfigurationAsync();

        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());
        await runtime.BeatAsync(printerOnline: true);
        runtime.Availability.Should().Be(KioskAvailability.Degraded);

        handler.OnHeartbeat(_ => ScriptedHandler.Json(Heartbeat(Start)));
        (await runtime.BeatAsync(printerOnline: true)).Should().BeTrue();

        runtime.Availability.Should().Be(KioskAvailability.Online);
    }

    [Fact]
    public async Task A_changed_configuration_is_fetched_again()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(printer: null)));
        await runtime.LoadConfigurationAsync();

        handler.ConfigCalls.Should().Be(1);

        // The first heartbeat establishes what the kiosk has seen.
        handler.OnHeartbeat(_ => ScriptedHandler.Json(Heartbeat(Start)));
        await runtime.BeatAsync(printerOnline: false);

        handler.ConfigCalls.Should().Be(1, "nothing has changed yet");

        // An admin picks a printer, which moves the timestamp.
        handler.OnHeartbeat(_ => ScriptedHandler.Json(Heartbeat(Start.AddMinutes(5))));
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration(printer: "New printer")));

        await runtime.BeatAsync(printerOnline: true);

        // Which reaches the kiosk without it polling the whole document.
        handler.ConfigCalls.Should().Be(2);
        runtime.Configuration!.SelectedPrinter.Should().Be("New printer");
    }

    [Fact]
    public async Task An_unchanged_configuration_is_not_fetched_again()
    {
        var (runtime, handler, _) = await CreateAsync();
        handler.OnConfig(_ => ScriptedHandler.Json(Configuration()));
        await runtime.LoadConfigurationAsync();

        for (var i = 0; i < 3; i++)
        {
            handler.OnHeartbeat(_ => ScriptedHandler.Json(Heartbeat(Start)));
            await runtime.BeatAsync(printerOnline: true);
        }

        handler.HeartbeatCalls.Should().Be(3);
        handler.ConfigCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_kiosk_with_no_configuration_at_all_is_offline()
    {
        var (runtime, handler, _) = await CreateAsync();

        handler.OnHeartbeat(_ => ScriptedHandler.Unreachable());
        await runtime.BeatAsync(printerOnline: false);

        // Activated but never configured: there is nothing to work from.
        runtime.Configuration.Should().BeNull();
        runtime.Availability.Should().Be(KioskAvailability.Offline);
    }
}
