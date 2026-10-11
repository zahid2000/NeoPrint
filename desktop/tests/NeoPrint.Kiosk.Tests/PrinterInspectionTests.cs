using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Platform.Linux;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Asking printers about themselves (plan section 7.10): which source is
/// asked, what happens when one cannot be, and how often.
/// </summary>
public sealed class PrinterInspectionTests
{
    /// <summary>Answers CUPS's commands from a script, and counts the calls.</summary>
    private sealed class Commands : ICommandRunner
    {
        public Dictionary<string, (int Exit, string Output)> Answers { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add($"{program} {string.Join(' ', arguments)}");

            return Answers.TryGetValue(program, out var answer)
                ? Task.FromResult(answer)
                : throw new System.ComponentModel.Win32Exception(2, $"{program}: not found");
        }
    }

    private static RoutingPrinterInspector Routing(IPrinterInspector? machine, FakePrinterService? fake = null)
        => new(machine, fake ?? new FakePrinterService(), NullLogger<RoutingPrinterInspector>.Instance);

    [Fact]
    public async Task On_linux_a_printer_is_asked_over_ipp_and_its_queue_through_lpstat()
    {
        var commands = new Commands
        {
            Answers =
            {
                ["ipptool"] = (0,
                    "        printer-make-and-model (textWithoutLanguage) = HP LaserJet Pro M404dn\n"
                    + "        device-uri (uri) = usb://HP/LaserJet%20Pro%20M404dn?serial=PHB1234\n"
                    + "        printer-state (enum) = processing\n"
                    + "        printer-state-reasons (1setOf keyword) = none\n"
                    + "        marker-names (1setOf nameWithoutLanguage) = Black Cartridge\n"
                    + "        marker-types (1setOf keyword) = toner\n"
                    + "        marker-levels (1setOf integer) = 63\n"),
                ["lpstat"] = (0, "HP_M404-41              kiosk            20480   Sun 11 Oct 2026 12:00:00\n"),
            },
        };

        var details = await new LinuxPrinterInspector(commands).InspectAsync("HP_M404");

        details.Model.Should().Be("HP LaserJet Pro M404dn");
        details.Connection.Should().Be(PrinterConnection.Usb);
        details.Supplies.Should().ContainSingle().Which.LevelPercent.Should().Be(63);
        details.SpoolerJobs.Should().ContainSingle().Which.Id.Should().Be("HP_M404-41");

        commands.Calls.Should().Contain(call => call.StartsWith("ipptool", StringComparison.Ordinal) && call.Contains("ipp://localhost/printers/HP_M404"));
        commands.Calls.Should().Contain("lpstat -W not-completed -o HP_M404");
    }

    [Fact]
    public async Task Without_the_ipp_tools_a_printer_is_reported_by_name_alone()
    {
        var details = await new LinuxPrinterInspector(new Commands()).InspectAsync("HP_M404");

        details.Name.Should().Be("HP_M404");
        details.Model.Should().BeNull();
        details.State.Should().BeNull();
        details.Supplies.Should().BeNull();
    }

    [Fact]
    public async Task The_fake_printer_answers_for_itself()
    {
        var fake = new FakePrinterService { Counter = new PageCount(1200, CountsSheets: false) };

        var details = await Routing(machine: null, fake).InspectAsync(FakePrinterService.FakePrinterName);

        details.Manufacturer.Should().Be("NeoPrint");
        details.State.Should().Be("idle");
        details.StateReasons.Should().BeEmpty();
        details.Online.Should().BeTrue();
        details.LifetimePages.Should().Be(1200);
        details.Features!.PaperSizes.Should().Equal("A4", "A3");

        fake.SimulatedState = PrinterState.OutOfPaper;
        var empty = await Routing(null, fake).InspectAsync(FakePrinterService.FakePrinterName);
        empty.State.Should().Be("stopped");
        empty.StateReasons.Should().Equal("media-empty");
        empty.Online.Should().BeTrue("out of paper is not offline");
    }

    [Fact]
    public async Task A_machine_printer_goes_to_the_machine_inspector_and_a_failure_there_is_not_an_exception()
    {
        var asked = new List<string>();
        var machine = new Scripted(name =>
        {
            asked.Add(name);
            return name == "Broken" ? throw new InvalidOperationException("WMI is not answering") : new PrinterDetails(name, Model: "Real one");
        });

        var routing = Routing(machine);

        (await routing.InspectAsync("Office printer")).Model.Should().Be("Real one");
        (await routing.InspectAsync("Broken")).Should().Be(new PrinterDetails("Broken"));
        (await Routing(machine: null).InspectAsync("Nobody to ask")).Should().Be(new PrinterDetails("Nobody to ask"));

        asked.Should().Equal("Office printer", "Broken");
    }

    private sealed class Scripted(Func<string, PrinterDetails> answer) : IPrinterInspector
    {
        public Task<PrinterDetails> InspectAsync(string printer, CancellationToken cancellationToken = default)
            => Task.FromResult(answer(printer));
    }

    [Fact]
    public void The_selected_printer_is_asked_every_time_and_the_others_once_in_ten_minutes()
    {
        var schedule = new PrinterTelemetrySchedule();
        var start = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);
        string[] discovered = ["Main", "Spare", "PDF"];

        schedule.Due("Main", discovered, start).Should().Equal("Main", "Spare", "PDF");
        schedule.Due("Main", discovered, start.AddMinutes(1)).Should().Equal("Main");
        schedule.Due("Main", discovered, start.AddMinutes(9)).Should().Equal("Main");
        schedule.Due("Main", discovered, start.AddMinutes(10)).Should().Equal("Main", "Spare", "PDF");
        schedule.Due("Main", discovered, start.AddMinutes(11)).Should().Equal("Main");
    }

    [Fact]
    public void With_no_printer_selected_only_the_ten_minute_round_asks_anything()
    {
        var schedule = new PrinterTelemetrySchedule();
        var start = new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

        schedule.Due(null, ["Spare"], start).Should().Equal("Spare");
        schedule.Due(null, ["Spare"], start.AddMinutes(1)).Should().BeEmpty();
        schedule.Due("Gone", [], start.AddMinutes(2)).Should().ContainSingle()
            .Which.Should().Be("Gone", "a selected printer that is no longer found is still asked about");
    }
}
