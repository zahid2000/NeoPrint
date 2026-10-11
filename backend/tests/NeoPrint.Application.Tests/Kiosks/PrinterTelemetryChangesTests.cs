using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Tests.Kiosks;

/// <summary>
/// What a printer's readings say happened to it, and when a spooler job is
/// stuck (plan section 7.10).
/// </summary>
public sealed class PrinterTelemetryChangesTests
{
    private static readonly DateTime Start = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private static PrinterTelemetrySample Sample(
        int minute,
        string? state = "idle",
        bool? online = true,
        string[]? reasons = null,
        string? error = null,
        int? black = 40,
        params PrinterSpoolerJob[] jobs)
        => new()
        {
            Timestamp = Start.AddMinutes(minute),
            Printer = new PrinterKey { KioskId = Guid.Empty, PrinterName = "P" },
            State = state,
            Online = online,
            StateReasons = [.. reasons ?? []],
            LastError = error,
            Supplies = [new PrinterSupplyLevel("Black", "toner", "black", black)],
            SpoolerJobs = [.. jobs],
        };

    [Fact]
    public void Readings_that_do_not_differ_are_no_events()
        => PrinterTelemetryChanges.Between([Sample(0), Sample(1), Sample(2)]).Should().BeEmpty();

    [Fact]
    public void A_change_of_state_reasons_or_reachability_is_an_event_newest_first()
    {
        var events = PrinterTelemetryChanges.Between(
        [
            Sample(0),
            Sample(1, state: "printing"),
            Sample(2, state: "stopped", reasons: ["media-empty"], error: "Tray 1 is empty"),
            Sample(3, state: "stopped", online: false, reasons: ["media-empty"], error: "Tray 1 is empty"),
        ]);

        events.Select(e => (e.Kind, e.From, e.To)).Should().Equal(
            ("Online", "online", "offline"),
            ("State", "printing", "stopped"),
            ("Reasons", null, "media-empty"),
            ("Error", null, "Tray 1 is empty"),
            ("State", "idle", "printing"));

        events[0].At.Should().Be(Start.AddMinutes(3));
    }

    [Fact]
    public void A_level_that_jumps_up_is_a_replaced_cartridge_and_a_wobble_is_not()
    {
        var events = PrinterTelemetryChanges.Between([Sample(0, black: 4), Sample(1, black: 6), Sample(2, black: 100), Sample(3, black: 99)]);

        var replaced = events.Should().ContainSingle().Which;
        replaced.Kind.Should().Be("SupplyReplaced");
        replaced.From.Should().Be("Black: 6%");
        replaced.To.Should().Be("Black: 100%");
    }

    [Fact]
    public void A_level_that_becomes_known_or_unknown_is_not_a_replacement()
        => PrinterTelemetryChanges.Between([Sample(0, black: null), Sample(1, black: 90), Sample(2, black: null)]).Should().BeEmpty();

    // -----------------------------------------------------------------------
    // The spooler
    // -----------------------------------------------------------------------

    [Fact]
    public void A_job_the_spooler_has_had_for_five_minutes_is_stuck()
    {
        var submitted = Start.AddMinutes(-6);

        var stuck = PrinterTelemetryChanges.StuckJob(null, Sample(0, jobs: [new PrinterSpoolerJob("P-17", 1024, "processing", submitted)]));

        stuck.Should().NotBeNull();
        stuck!.Value.Job.Id.Should().Be("P-17");
        stuck.Value.For.Should().Be(TimeSpan.FromMinutes(6));

        PrinterTelemetryChanges.StuckJob(null, Sample(0, jobs: [new PrinterSpoolerJob("P-18", 1024, "processing", Start.AddMinutes(-4))]))
            .Should().BeNull("four minutes is a long document, not a stuck one");
    }

    [Fact]
    public void A_job_without_a_time_is_timed_from_the_first_reading_that_had_it()
    {
        var job = new PrinterSpoolerJob("P-20", null, "pending", SubmittedAt: null);

        var first = Sample(0, jobs: [job]);
        PrinterTelemetryChanges.StuckJob(null, first).Should().BeNull();
        first.JobsSeenSince["P-20"].Should().Be(Start);

        var second = Sample(3, jobs: [job]);
        PrinterTelemetryChanges.StuckJob(first, second).Should().BeNull();

        var third = Sample(5, jobs: [job]);
        PrinterTelemetryChanges.StuckJob(second, third)!.Value.For.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void A_job_that_left_the_spooler_is_forgotten()
    {
        var first = Sample(0, jobs: [new PrinterSpoolerJob("P-21", null, "pending", null)]);
        PrinterTelemetryChanges.StuckJob(null, first);

        var second = Sample(6);
        PrinterTelemetryChanges.StuckJob(first, second).Should().BeNull();
        second.JobsSeenSince.Should().BeEmpty();
    }

    [Fact]
    public void Of_several_stuck_jobs_the_one_that_waited_longest_is_named()
    {
        var stuck = PrinterTelemetryChanges.StuckJob(
            null,
            Sample(
                0,
                jobs:
                [
                    new PrinterSpoolerJob("P-30", null, "pending", Start.AddMinutes(-7)),
                    new PrinterSpoolerJob("P-29", null, "processing", Start.AddMinutes(-12)),
                ]));

        stuck!.Value.Job.Id.Should().Be("P-29");
    }

    // -----------------------------------------------------------------------
    // What a kiosk relays from a printer is bounded
    // -----------------------------------------------------------------------

    [Fact]
    public void What_a_kiosk_relays_from_a_printer_is_bounded_before_it_is_stored()
    {
        var sample = PrinterTelemetryHandlers.Clean(
            new PrinterTelemetryReport(
                "  HP LaserJet\r\n",
                Model: new string('m', 400),
                Connection: "Bluetooth",
                State: "on fire",
                StateReasons: ["toner-low", "toner-low", string.Empty, new string('r', 200)],
                Supplies:
                [
                    new PrinterSupplyLevel("Black", "toner", "black", 140),
                    new PrinterSupplyLevel("  ", "toner", null, 50),
                    new PrinterSupplyLevel("Glitter", "confetti", null, 30),
                ],
                Trays: [new PrinterTrayLevel("Tray 1", "A4", -3)],
                LifetimePages: -5,
                Features: new PrinterFeatureSet { ResolutionDpi = -600, PaperSizes = ["A4", "A4", "A3"] },
                SpoolerJobs: [new PrinterSpoolerJob("P-1", -10, "pending", Start.AddYears(3))]),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Start)!;

        sample.Printer.PrinterName.Should().Be("HP LaserJet", "control characters never reach a log line or a page");
        sample.Model.Should().HaveLength(128);
        sample.Connection.Should().BeNull();
        sample.State.Should().BeNull();
        sample.StateReasons.Should().HaveCount(2).And.Contain("toner-low");
        sample.StateReasons[1].Should().HaveLength(64);
        sample.Supplies.Should().Equal(
            new PrinterSupplyLevel("Black", "toner", "black", null),
            new PrinterSupplyLevel("Glitter", "other", null, 30));
        sample.Trays.Should().Equal(new PrinterTrayLevel("Tray 1", "A4", null));
        sample.LifetimePages.Should().BeNull();
        sample.Features!.ResolutionDpi.Should().BeNull();
        sample.Features.PaperSizes.Should().Equal("A4", "A3");
        sample.SpoolerJobs.Should().Equal(new PrinterSpoolerJob("P-1", null, "pending", null));

        PrinterTelemetryHandlers.Clean(new PrinterTelemetryReport("   "), Guid.NewGuid(), Guid.NewGuid(), Start)
            .Should().BeNull("a reading that names no printer is about nothing");
    }
}
