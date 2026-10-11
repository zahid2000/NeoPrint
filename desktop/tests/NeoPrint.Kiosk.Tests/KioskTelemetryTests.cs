using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Platform.Linux;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Kiosk telemetry (plan section 7.9; the tests plan section 11 asks for:
/// the <c>/proc</c> parsers and what a report is made of).
/// </summary>
public sealed class KioskTelemetryTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = Start;
    }

    // -----------------------------------------------------------------------
    // /proc, as a real machine writes it
    // -----------------------------------------------------------------------

    private const string Stat =
        "cpu  4705 356 584 3699 23 23 0 0 0 0\n"
        + "cpu0 1393280 32966 572056 13343292 6130 0 17875 0 23933 0\n"
        + "intr 1462898\n"
        + "ctxt 115315\n";

    [Fact]
    public void The_processor_times_are_read_from_the_first_line_of_proc_stat()
    {
        // user 4705 + nice 356 + system 584 + idle 3699 + iowait 23 + irq 23 = 9390; idle + iowait = 3722.
        ProcParsers.CpuTimes(Stat).Should().Be((3722L, 9390L));

        ProcParsers.CpuTimes("intr 5\n").Should().BeNull();
        ProcParsers.CpuTimes("cpu  12 x 3 4\n").Should().BeNull("a line that is not numbers is not a reading");
        ProcParsers.CpuTimes(null).Should().BeNull();
    }

    [Fact]
    public void Processor_use_is_what_was_not_idle_between_two_readings()
    {
        SystemProbes.CpuPercent(null, (100, 1000)).Should().BeNull("one reading says nothing about use");
        SystemProbes.CpuPercent((100, 1000), (150, 1200)).Should().Be(75.0);
        SystemProbes.CpuPercent((100, 1000), (300, 1200)).Should().Be(0.0);
        SystemProbes.CpuPercent((100, 1000), (100, 1000)).Should().BeNull("no time passed");
        SystemProbes.CpuPercent((500, 5000), (10, 100)).Should().BeNull("the counters started again after a reboot");
    }

    [Fact]
    public void Memory_in_use_is_total_minus_what_the_kernel_calls_available()
    {
        const string meminfo =
            "MemTotal:        8040044 kB\n"
            + "MemFree:          553100 kB\n"
            + "MemAvailable:    5030044 kB\n"
            + "Buffers:          210812 kB\n"
            + "Cached:          4012348 kB\n";

        // Not total minus free: the page cache is given back when it is needed.
        ProcParsers.Memory(meminfo).Should().Be((3010000L * 1024, 8040044L * 1024));

        ProcParsers.Memory("MemTotal: 100 kB\n").Should().BeNull("an old kernel without MemAvailable is not guessed at");
        ProcParsers.Memory(string.Empty).Should().BeNull();
    }

    [Fact]
    public void Uptime_the_system_name_and_the_wifi_signal_are_read_from_their_files()
    {
        ProcParsers.UptimeSeconds("350735.47 234388.90\n").Should().Be(350735);
        ProcParsers.UptimeSeconds("not a number").Should().BeNull();

        ProcParsers.OsName("NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n").Should().Be("Ubuntu 24.04.1 LTS");
        ProcParsers.OsName("NAME=Alpine\n").Should().BeNull();

        const string wireless =
            "Inter-| sta-|   Quality        |   Discarded packets               | Missed | WE\n"
            + " face | tus | link level noise |  nwid  crypt   frag  retry   misc | beacon | 22\n"
            + " wlan0: 0000   54.  -56.  -256        0      0      0      0      0        0\n";

        ProcParsers.WifiSignalPercent(wireless, "wlan0").Should().Be(77, "54 out of 70");
        ProcParsers.WifiSignalPercent(wireless, "wlan1").Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // The counters
    // -----------------------------------------------------------------------

    [Fact]
    public void Jobs_and_sheets_are_counted_for_the_kiosk_own_day()
    {
        var clock = new TestClock();
        var counters = new TelemetryCounters(clock);

        counters.JobFinished(printedSheets: 3);
        counters.JobFinished(printedSheets: 0);
        counters.JobFinished(printedSheets: 5);

        counters.Today.Should().Be((2, 8), "a job that printed nothing is not a printed job");

        clock.UtcNow = Start.AddDays(1);

        counters.Today.Should().Be((0, 0), "yesterday's work is not today's");
    }

    [Fact]
    public void The_job_on_the_printer_and_the_last_scan_are_remembered()
    {
        var counters = new TelemetryCounters(new TestClock());

        counters.CurrentJob.Should().BeNull();
        counters.QrLastReadAt.Should().BeNull();

        counters.Printing("482913");
        counters.QrRead(Start.AddMinutes(-4));

        counters.CurrentJob.Should().Be("482913");
        counters.QrLastReadAt.Should().Be(Start.AddMinutes(-4));

        counters.Printing(null);
        counters.CurrentJob.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // The report
    // -----------------------------------------------------------------------

    private static KioskTelemetryCollector Collector(TestClock clock, TelemetryCounters counters, FakeSystemMetrics? metrics = null)
    {
        var paths = new FakeAppPaths();
        var api = new KioskApiClient(new HttpClient(), new FakeDeviceKeyStore(paths), NullLogger<KioskApiClient>.Instance);
        var runtime = new KioskRuntime(api, clock, NullLogger<KioskRuntime>.Instance);

        return new KioskTelemetryCollector(metrics ?? new FakeSystemMetrics(), runtime, counters, clock);
    }

    [Fact]
    public async Task A_report_carries_the_machine_the_screen_and_the_work()
    {
        var clock = new TestClock();
        var counters = new TelemetryCounters(clock);
        var collector = Collector(clock, counters);

        counters.JobFinished(4);
        counters.Printing("771204");
        clock.UtcNow = Start.AddMinutes(10);

        var report = await collector.CollectAsync(new TelemetrySources
        {
            Screen = () => new TelemetryScreenState("Flow.Options", "Anonymous", QueueLength: 3, ScreenWidth: 1920, ScreenHeight: 1080),
            Hub = () => new HubState(Connected: true, LastReconnectAt: Start.AddMinutes(2), DisconnectedSince: null),
            FlashInserted = _ => Task.FromResult<bool?>(true),
            SpoolFiles = () => 2,
            FailedFlashUploads = () => 1,
            PendingCommands = () => 0,
            LibreOffice = () => true,
            Ghostscript = () => false,
        });

        report.SentAt.Should().Be(Start.AddMinutes(10));
        report.App.StartedAt.Should().Be(Start, "the application started when the collector was made");
        report.App.Version.Should().NotBeNullOrEmpty();
        report.App.Screen.Should().Be("Flow.Options");
        report.App.Session.Should().Be("Anonymous");

        report.System.Hostname.Should().Be("fake-kiosk");
        report.System.MemoryTotalBytes.Should().Be(8L * 1024 * 1024 * 1024);
        report.Network.ConnectionType.Should().Be("Ethernet");
        report.Network.HubConnected.Should().BeTrue();
        report.Network.HubLastReconnectAt.Should().Be(Start.AddMinutes(2));
        report.Network.HubDisconnectedSeconds.Should().BeNull();
        report.Network.BackendLatencyMs.Should().BeNull("no heartbeat has been answered yet");

        report.Peripherals.FlashInserted.Should().BeTrue();
        report.Peripherals.ScreenWidth.Should().Be(1920);

        report.Work.QueueLength.Should().Be(3);
        report.Work.CurrentJob.Should().Be("771204");
        report.Work.JobsToday.Should().Be(1);
        report.Work.SheetsToday.Should().Be(4);
        report.Work.SpoolFiles.Should().Be(2);
        report.Work.FailedFlashUploads.Should().Be(1);

        report.Services.LibreOffice.Should().BeTrue();
        report.Services.Ghostscript.Should().BeFalse();
    }

    [Fact]
    public async Task A_dropped_connection_is_reported_with_how_long_it_has_been_down()
    {
        var clock = new TestClock { UtcNow = Start.AddMinutes(5) };
        var collector = Collector(clock, new TelemetryCounters(clock));

        var report = await collector.CollectAsync(new TelemetrySources
        {
            Hub = () => new HubState(Connected: false, LastReconnectAt: null, DisconnectedSince: Start.AddMinutes(2)),
        });

        report.Network.HubConnected.Should().BeFalse();
        report.Network.HubDisconnectedSeconds.Should().Be(180);
    }

    [Fact]
    public async Task What_the_machine_cannot_tell_is_left_out_not_made_up()
    {
        var clock = new TestClock();
        var collector = Collector(clock, new TelemetryCounters(clock), new FakeSystemMetrics { Snapshot = new SystemSnapshot() });

        var report = await collector.CollectAsync(new TelemetrySources());

        report.System.CpuPercent.Should().BeNull();
        report.System.MemoryTotalBytes.Should().BeNull();
        report.System.SpoolDiskFreeBytes.Should().BeNull();
        report.Network.ConnectionType.Should().BeNull();
        report.Network.HubConnected.Should().BeNull();
        report.Peripherals.FlashInserted.Should().BeNull();
        report.Work.SpoolFiles.Should().BeNull();
        report.Services.LibreOffice.Should().BeNull();
        report.Services.FeatureFlags.Should().BeNull();
    }

    [Fact]
    public void The_feature_fingerprint_tells_two_sets_of_flags_apart_whatever_their_order()
    {
        var one = KioskTelemetryCollector.FeatureFingerprint(new Dictionary<string, bool> { ["scan"] = true, ["wallet"] = false });
        var same = KioskTelemetryCollector.FeatureFingerprint(new Dictionary<string, bool> { ["wallet"] = false, ["scan"] = true });
        var other = KioskTelemetryCollector.FeatureFingerprint(new Dictionary<string, bool> { ["scan"] = true, ["wallet"] = true });

        one.Should().HaveLength(8).And.Be(same).And.NotBe(other);
        KioskTelemetryCollector.FeatureFingerprint(null).Should().BeNull();
    }

    [Fact]
    public void The_free_space_of_a_real_folder_is_read_from_its_volume()
    {
        // The real file system, wherever the tests run: a number, not an exception.
        SystemProbes.FreeBytes(Path.GetTempPath()).Should().BeGreaterThan(0);
        SystemProbes.FreeBytes(Path.Combine(Path.GetTempPath(), "does", "not", "exist")).Should().BeGreaterThan(0, "the volume is what is asked about");
    }
}
