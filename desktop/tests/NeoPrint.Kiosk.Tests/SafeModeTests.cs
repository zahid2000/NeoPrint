using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Kiosk.Agent;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// A kiosk UI that keeps crashing starts on the maintenance screen and takes
/// no orders (plan section 7.12, step 5; <c>Kiosk.CrashLoopThreshold</c>).
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class SafeModeTests : IDisposable
{
    private static readonly DateTime Booted = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeAppPaths _paths = new();
    private readonly TestClock _clock = new() { UtcNow = Booted.AddHours(1) };

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private CrashLoopGuard Guard(DateTime? bootedAt = null) => new(_paths, _clock, threshold: 3, () => bootedAt ?? Booted);

    /// <summary>Starts a run and lets it die without saying goodbye, a restart delay later.</summary>
    private bool CrashAndRestart(CrashLoopGuard guard)
    {
        _clock.UtcNow += TimeSpan.FromSeconds(5);
        return guard.Start();
    }

    [Fact]
    public void Three_crashes_in_ten_minutes_start_the_ui_in_safe_mode()
    {
        var guard = Guard();

        guard.Start().Should().BeFalse("the first start is a start");
        CrashAndRestart(guard).Should().BeFalse("one crash");
        CrashAndRestart(guard).Should().BeFalse("two crashes");
        CrashAndRestart(guard).Should().BeTrue("the third crash in ten minutes is a loop");

        guard.SafeModeSince.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public void A_run_that_follows_a_crash_knows_it_and_one_that_follows_a_clean_exit_does_not()
    {
        var guard = Guard();

        guard.Start();
        guard.CrashedLastRun.Should().BeFalse("the first start follows nothing");

        // What the kiosk reports to the backend as an incident (plan section 7.1).
        var afterCrash = Guard();
        _clock.UtcNow += TimeSpan.FromSeconds(5);
        afterCrash.Start();
        afterCrash.CrashedLastRun.Should().BeTrue();

        afterCrash.Stop();

        var afterCleanExit = Guard();
        _clock.UtcNow += TimeSpan.FromSeconds(5);
        afterCleanExit.Start();
        afterCleanExit.CrashedLastRun.Should().BeFalse("closing on purpose is not a crash");
    }

    [Fact]
    public void A_kiosk_that_was_told_to_stop_three_times_is_not_in_a_crash_loop()
    {
        // T-236: systemd, the Agent or an operator restarting the service
        // sends SIGTERM; what the kiosk does then is this note.
        var guard = Guard();
        guard.Start();

        for (var restart = 0; restart < 3; restart++)
        {
            TerminationSignal.Note(guard.Stop);

            var next = Guard();
            _clock.UtcNow += TimeSpan.FromSeconds(5);
            next.Start().Should().BeFalse("restarted on request, not crashed");
            next.CrashedLastRun.Should().BeFalse();
            guard = next;
        }

        guard.LastCrashAt.Should().BeNull();
    }

    [Fact]
    public void A_note_that_cannot_be_written_does_not_keep_the_kiosk_from_stopping()
    {
        var note = () => TerminationSignal.Note(() => throw new IOException("the disk is gone"));

        note.Should().NotThrow();
    }

    [Fact]
    public void The_kiosk_listens_for_the_request_to_stop()
    {
        var noted = 0;

        using var registration = TerminationSignal.OnRequested(() => noted++);

        registration.Should().NotBeNull();
        noted.Should().Be(0, "nothing is noted until the system asks");
    }

    [Fact]
    public void Clean_exits_and_crashes_spread_over_hours_are_not_a_loop()
    {
        var guard = Guard();

        for (var run = 0; run < 5; run++)
        {
            guard.Start().Should().BeFalse();
            guard.Stop();
        }

        for (var crash = 0; crash < 5; crash++)
        {
            _clock.UtcNow += TimeSpan.FromMinutes(6);
            guard.Start().Should().BeFalse("never three within ten minutes");
        }
    }

    [Fact]
    public void A_reboot_or_a_power_cut_is_not_a_crash()
    {
        var guard = Guard();
        guard.Start();

        for (var outage = 0; outage < 3; outage++)
        {
            // The machine came back up after the run started: the run ended
            // with the power, not with a fault.
            _clock.UtcNow += TimeSpan.FromMinutes(1);
            Guard(bootedAt: _clock.UtcNow.AddSeconds(-30)).Start().Should().BeFalse();
        }
    }

    [Fact]
    public void Safe_mode_stays_across_restarts_until_it_is_cleared()
    {
        var guard = Guard();
        guard.Start();
        CrashAndRestart(guard);
        CrashAndRestart(guard);
        CrashAndRestart(guard).Should().BeTrue();

        guard.Stop();
        _clock.UtcNow += TimeSpan.FromHours(2);
        guard.Start().Should().BeTrue("restarting into the same fault would only loop again");

        guard.Clear();
        guard.Stop();
        guard.Start().Should().BeFalse();
    }

    [Fact]
    public void A_damaged_record_starts_the_count_again()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Path.Combine(_paths.StateDirectory, "runs.json"), "{ not json");

        Guard().Start().Should().BeFalse();
    }

    [Fact]
    public void The_safe_mode_screen_is_not_replaced_by_configuration_availability_or_the_printer()
    {
        var model = new MainWindowViewModel();
        model.EnterSafeMode();

        model.ApplyConfiguration(Configuration());
        model.ApplyAvailability(KioskAvailability.Online, hasConfiguration: true);
        model.ApplyAvailability(KioskAvailability.Offline, hasConfiguration: false);
        model.SetPrinterProblem("printer.outOfPaper");
        model.SetPrinterProblem(null);

        model.Screen.Should().Be(KioskScreen.SafeMode);
        model.CanPrintFromFlash.Should().BeFalse("safe mode takes no orders");
        model.SafeModeTitle.Should().Be(Localization.Strings.Get(model.Language, "safeMode.title"));
    }

    [Fact]
    public async Task The_heartbeat_tells_the_backend_about_safe_mode()
    {
        var handler = new HeartbeatRecorder();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://backend.test/") };
        var api = new KioskApiClient(http, new FakeDeviceKeyStore(_paths), NullLogger<KioskApiClient>.Instance);
        await api.RegisterAsync(
            new MachineFingerprint("test-host", "Linux", "6.1", "machine-1", [], "CPU", 8, "1920x1080"), "Linux", "0.3.0", null, []);

        var runtime = new KioskRuntime(api, _clock, NullLogger<KioskRuntime>.Instance) { SafeMode = true };

        await runtime.BeatAsync(printerOnline: true);

        handler.Heartbeats.Should().ContainSingle().Which.Should().Contain("\"safeMode\":true");
    }

    [Fact]
    public void The_agent_keeps_restarting_a_crash_looping_ui_but_slowly()
    {
        var watchdog = new KioskUiWatchdog(
            Options.Create(new AgentOptions { RestartDelaySeconds = 3, CrashLoopThreshold = 3, CrashLoopRestartDelaySeconds = 60 }),
            NullLogger<KioskUiWatchdog>.Instance);
        var now = DateTimeOffset.UtcNow;

        watchdog.RecordExit(now);
        watchdog.RestartDelay().Should().Be(TimeSpan.FromSeconds(3));

        watchdog.RecordExit(now.AddSeconds(10));
        watchdog.RecordExit(now.AddSeconds(20));

        watchdog.IsCrashLooping.Should().BeTrue();
        watchdog.RestartDelay().Should().Be(TimeSpan.FromSeconds(60), "the UI comes back in safe mode, not in a tight loop");

        watchdog.RecordExit(now.AddMinutes(30));
        watchdog.IsCrashLooping.Should().BeFalse("the old exits fell out of the ten-minute window");
    }

    private static KioskConfiguration Configuration() => new(
        Guid.NewGuid(), "KSK-0001", "Mərkəz", "Online", "az", "HP", new PrinterCapabilityFlags(true, true, false), [], null, 5, 50, 200, null,
        new Dictionary<string, PaperStockLevel>(), new Dictionary<string, bool>(), 15, 24, DateTime.UtcNow);

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; }
    }

    /// <summary>Answers registration and tokens, and keeps each heartbeat body.</summary>
    private sealed class HeartbeatRecorder : HttpMessageHandler
    {
        public List<string> Heartbeats { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            string body = path switch
            {
                _ when path.EndsWith("/register", StringComparison.Ordinal) =>
                    $$"""{"deviceId":"{{Guid.NewGuid()}}","activated":true,"kioskId":"{{Guid.NewGuid()}}","kioskCode":"KSK-0001"}""",
                _ when path.EndsWith("/challenge", StringComparison.Ordinal) || path.EndsWith("/token", StringComparison.Ordinal) =>
                    // Good for an hour from whenever the test runs - not until a date written here.
                    $$"""{"accessToken":"kiosk-token","expiresAt":"{{DateTime.UtcNow.AddHours(1):O}}","tokenType":"kiosk","nonce":"nonce"}""",
                _ when path.EndsWith("/heartbeat", StringComparison.Ordinal) =>
                    """{"status":"Maintenance","nextInSeconds":15,"configurationChangedAt":"2026-09-28T00:00:00Z","serverTime":"2026-09-28T00:00:00Z"}""",
                _ => "{}",
            };

            if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                Heartbeats.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
