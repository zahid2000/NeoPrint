using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Tests.Kiosks;

/// <summary>
/// The limits of plan section 7.9: disk below 2 GB → Medium; memory above 90%
/// for five minutes → Medium; the backend slower than 2000 ms for five
/// minutes → Medium; SignalR down for two minutes → High.
/// </summary>
public sealed class TelemetryAlertEvaluatorTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly DateTime Start = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private static KioskTelemetrySample Sample(
        int secondsIn,
        long? systemDisk = 50 * Gb,
        long? spoolDisk = 50 * Gb,
        double memoryPercent = 40,
        int? latencyMs = 80,
        bool? hub = true,
        int? hubDownSeconds = null,
        long? clockOffsetMs = 150)
        => new()
        {
            Timestamp = Start.AddSeconds(secondsIn),
            System = new TelemetrySystem
            {
                SystemDiskFreeBytes = systemDisk,
                SpoolDiskFreeBytes = spoolDisk,
                MemoryTotalBytes = 8 * Gb,
                MemoryUsedBytes = (long)(8 * Gb * memoryPercent / 100),
                ClockOffsetMs = clockOffsetMs,
            },
            Network = new TelemetryNetwork { BackendLatencyMs = latencyMs, HubConnected = hub, HubDisconnectedSeconds = hubDownSeconds },
        };

    /// <summary>Feeds samples one after another, as the handler does, and returns the last evaluation.</summary>
    private static TelemetryEvaluation Run(params KioskTelemetrySample[] samples)
    {
        TelemetryEvaluation? last = null;
        KioskTelemetrySample? previous = null;

        foreach (var sample in samples)
        {
            last = TelemetryAlertEvaluator.Evaluate(previous, sample);
            previous = sample;
        }

        return last!;
    }

    [Fact]
    public void A_healthy_kiosk_raises_nothing()
    {
        var evaluation = Run(Sample(0), Sample(30), Sample(60));

        evaluation.Raise.Should().BeEmpty();
        evaluation.Cleared.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1L * 1024 * 1024 * 1024, 50L * 1024 * 1024 * 1024)]
    [InlineData(50L * 1024 * 1024 * 1024, 1900L * 1024 * 1024)]
    public void Less_than_2_gb_free_on_either_volume_is_raised_at_once(long system, long spool)
    {
        var evaluation = Run(Sample(0, systemDisk: system, spoolDisk: spool));

        var alert = evaluation.Raise.Should().ContainSingle().Which;
        alert.Type.Should().Be(IncidentType.DiskLow);
        alert.Severity.Should().Be(IncidentSeverity.Medium);
        alert.Details.Key.Should().Be("incidents.telemetry.diskLow");
    }

    [Fact]
    public void Exactly_2_gb_free_is_not_low_and_an_unknown_disk_is_not_guessed_at()
    {
        Run(Sample(0, systemDisk: 2 * Gb, spoolDisk: 2 * Gb)).Raise.Should().BeEmpty();
        Run(Sample(0, systemDisk: null, spoolDisk: null)).Raise.Should().BeEmpty();
    }

    [Fact]
    public void Memory_above_90_percent_is_raised_only_after_five_minutes_of_it()
    {
        var high = Enumerable.Range(0, 10).Select(i => Sample(i * 30, memoryPercent: 95)).ToArray();

        // 4 min 30 s in: over the limit all along, but not for long enough.
        Run(high).Raise.Should().BeEmpty();

        var evaluation = Run([.. high, Sample(300, memoryPercent: 95)]);

        var alert = evaluation.Raise.Should().ContainSingle().Which;
        alert.Type.Should().Be(IncidentType.MemoryHigh);
        alert.Severity.Should().Be(IncidentSeverity.Medium);
        alert.Details.Arguments["minutes"].Should().Be("5");
        alert.Details.Arguments["percent"].Should().Be("95");
    }

    [Fact]
    public void A_dip_below_the_limit_starts_the_five_minutes_again()
    {
        var samples = new List<KioskTelemetrySample>();

        for (var i = 0; i < 8; i++)
        {
            samples.Add(Sample(i * 30, memoryPercent: 95));
        }

        samples.Add(Sample(240, memoryPercent: 60));

        for (var i = 9; i <= 16; i++)
        {
            samples.Add(Sample(i * 30, memoryPercent: 95));
        }

        // Eight minutes after the first high sample, but only 3.5 since the dip.
        Run([.. samples]).Raise.Should().BeEmpty();
    }

    [Fact]
    public void A_slow_backend_is_raised_after_five_minutes()
    {
        var slow = Enumerable.Range(0, 11).Select(i => Sample(i * 30, latencyMs: 3500)).ToArray();

        var alert = Run(slow).Raise.Should().ContainSingle().Which;
        alert.Type.Should().Be(IncidentType.BackendSlow);
        alert.Severity.Should().Be(IncidentSeverity.Medium);

        Run(Sample(0, latencyMs: 2000), Sample(300, latencyMs: 2000)).Raise.Should().BeEmpty("2000 ms is the limit, not over it");
    }

    [Fact]
    public void A_clock_a_minute_off_for_five_minutes_is_raised_whichever_way_it_is_off()
    {
        var ahead = Enumerable.Range(0, 11).Select(i => Sample(i * 30, clockOffsetMs: 95_000)).ToArray();

        var alert = Run(ahead).Raise.Should().ContainSingle().Which;
        alert.Type.Should().Be(IncidentType.ClockOff);
        alert.Severity.Should().Be(IncidentSeverity.Medium);
        alert.Details.Key.Should().Be("incidents.telemetry.clockAhead");
        alert.Details.Arguments!["seconds"].Should().Be("95");

        var behind = Enumerable.Range(0, 11).Select(i => Sample(i * 30, clockOffsetMs: -60_000)).ToArray();
        Run(behind).Raise.Should().ContainSingle().Which.Details.Key.Should().Be("incidents.telemetry.clockBehind");

        // Back in time: cleared, so the incident closes.
        Run([.. ahead, Sample(330, clockOffsetMs: 400)]).Cleared.Should().Equal(TelemetryCondition.ClockOff);
    }

    [Fact]
    public void A_report_that_was_slow_to_arrive_is_not_a_wrong_clock()
    {
        // Under a minute is within the limit however long it lasts: the
        // offset includes the time the report took on the network.
        var nearly = Enumerable.Range(0, 30).Select(i => Sample(i * 30, clockOffsetMs: -59_999)).ToArray();
        Run(nearly).Raise.Should().BeEmpty();

        // One report held up for two minutes, among punctual ones.
        var held = Enumerable.Range(0, 30).Select(i => Sample(i * 30, clockOffsetMs: i == 12 ? -120_000 : -200)).ToArray();
        Run(held).Raise.Should().BeEmpty();

        // A minute off, but not yet for five minutes.
        var young = Enumerable.Range(0, 9).Select(i => Sample(i * 30, clockOffsetMs: 70_000)).ToArray();
        Run(young).Raise.Should().BeEmpty();

        // A kiosk that sent no time of its own is not judged.
        var silent = Enumerable.Range(0, 30).Select(i => Sample(i * 30, clockOffsetMs: null)).ToArray();
        Run(silent).Raise.Should().BeEmpty();
    }

    [Fact]
    public void A_dropped_live_connection_is_high_after_two_minutes()
    {
        var down = Enumerable.Range(0, 4).Select(i => Sample(i * 30, hub: false)).ToArray();

        Run(down).Raise.Should().BeEmpty("90 seconds so far");

        var alert = Run([.. down, Sample(120, hub: false)]).Raise.Should().ContainSingle().Which;
        alert.Type.Should().Be(IncidentType.ConnectionLost);
        alert.Severity.Should().Be(IncidentSeverity.High);
    }

    [Fact]
    public void The_kiosk_own_count_of_how_long_the_connection_has_been_down_is_believed()
    {
        // The first sample the server sees already says "down for three minutes".
        var alert = Run(Sample(0, hub: false, hubDownSeconds: 180)).Raise.Should().ContainSingle().Which;

        alert.Type.Should().Be(IncidentType.ConnectionLost);
        alert.Details.Arguments["minutes"].Should().Be("3");
    }

    [Fact]
    public void Silence_between_samples_breaks_a_streak()
    {
        // High before a ten-minute silence and high after it says nothing
        // about the ten minutes in between.
        var evaluation = Run(Sample(0, memoryPercent: 95), Sample(30, memoryPercent: 95), Sample(630, memoryPercent: 95));

        evaluation.Raise.Should().BeEmpty();
    }

    [Fact]
    public void A_limit_that_is_no_longer_exceeded_is_reported_as_cleared()
    {
        var evaluation = Run(Sample(0, spoolDisk: Gb), Sample(30, spoolDisk: 20 * Gb));

        evaluation.Raise.Should().BeEmpty();
        evaluation.Cleared.Should().Equal(TelemetryCondition.DiskLow);
    }

    [Fact]
    public void A_kiosk_that_comes_back_healthy_after_a_silence_still_clears_what_was_open()
    {
        var evaluation = Run(Sample(0, spoolDisk: Gb), Sample(3600, spoolDisk: 20 * Gb));

        evaluation.Cleared.Should().Equal(TelemetryCondition.DiskLow);
    }

    [Fact]
    public void Each_sample_carries_since_when_it_has_been_over_a_limit()
    {
        var first = Sample(0, memoryPercent: 95);
        var second = Sample(30, memoryPercent: 96);

        TelemetryAlertEvaluator.Evaluate(null, first);
        TelemetryAlertEvaluator.Evaluate(first, second);

        second.Breaches.Should().ContainKey(TelemetryCondition.MemoryHigh).WhoseValue.Should().Be(first.Timestamp);
    }

    [Fact]
    public void What_a_kiosk_sends_is_bounded_before_it_is_stored()
    {
        var now = Start;

        var sample = KioskTelemetryHandlers.Clean(
            new KioskTelemetryReport(
                now.AddMinutes(-2),
                new TelemetryApp { Version = new string('v', 500), Screen = "Home\r\nforged log line", Session = "Root", StartedAt = now.AddYears(5) },
                new TelemetrySystem { CpuPercent = 250, MemoryTotalBytes = 8 * Gb, MemoryUsedBytes = 99 * Gb, SystemDiskFreeBytes = -5 },
                new TelemetryNetwork { LocalIp = "<script>", WifiSignalPercent = 140, BackendLatencyMs = -1, ConnectionType = "Carrier pigeon" },
                new TelemetryPeripherals { ScreenWidth = 1920, ScreenHeight = -1 },
                new TelemetryWork { QueueLength = -3, JobsToday = 12 },
                null),
            Guid.NewGuid(),
            Guid.NewGuid(),
            now);

        sample.App.Version.Should().HaveLength(128);
        sample.App.Screen.Should().Be("Homeforged log line", "control characters never reach a log line");
        sample.App.Session.Should().BeNull();
        sample.App.StartedAt.Should().BeNull("a start time years ahead is not a time");
        sample.System.CpuPercent.Should().BeNull();
        sample.System.MemoryUsedBytes.Should().BeNull("more memory used than there is");
        sample.System.SystemDiskFreeBytes.Should().BeNull();
        sample.System.ClockOffsetMs.Should().Be(-120_000, "the kiosk's clock is two minutes behind");
        sample.Network.LocalIp.Should().BeNull();
        sample.Network.WifiSignalPercent.Should().BeNull();
        sample.Network.BackendLatencyMs.Should().BeNull();
        sample.Network.ConnectionType.Should().BeNull();
        sample.Peripherals.ScreenWidth.Should().Be(1920);
        sample.Peripherals.ScreenHeight.Should().BeNull();
        sample.Work.QueueLength.Should().BeNull();
        sample.Work.JobsToday.Should().Be(12);
        sample.Timestamp.Should().Be(now, "the series is ordered by the server's clock, not the kiosk's");
    }
}
