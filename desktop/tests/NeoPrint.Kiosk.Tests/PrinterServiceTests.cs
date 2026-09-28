using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Platform.Linux;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// CUPS as the Linux kiosk sees it, and the fake printer listed beside the
/// machine's own (plan sections 7.4, 7.6, 7.7).
/// </summary>
public sealed class PrinterServiceTests
{
    private const string Printer = "HP_LaserJet";

    [Theory]
    [InlineData(0, "printer HP_LaserJet is idle.  enabled since Mon\n\tAlerts: none", PrinterState.Online)]
    [InlineData(0, "printer HP_LaserJet is idle.  enabled since Mon\n\tAlerts: media-empty-error", PrinterState.OutOfPaper)]
    [InlineData(0, "printer HP_LaserJet is idle.\n\tAlerts: media-needed", PrinterState.OutOfPaper)]
    [InlineData(0, "printer HP_LaserJet is idle.\n\tAlerts: media-jam-error", PrinterState.PaperJam)]
    [InlineData(0, "printer HP_LaserJet disabled since Mon -\n\treason unknown", PrinterState.Offline)]
    [InlineData(0, "printer HP_LaserJet is idle.\n\tAlerts: offline-report", PrinterState.Offline)]
    [InlineData(0, "printer HP_LaserJet is idle.\n\tAlerts: marker-supply-low-warning", PrinterState.TonerLow)]
    [InlineData(0, "printer HP_LaserJet is idle.\n\tAlerts: cover-open-error", PrinterState.Error)]
    [InlineData(1, "", PrinterState.NotFound)]
    public void The_state_is_read_from_lpstat(int exitCode, string output, PrinterState expected)
        => LinuxPrinterService.ParseState(exitCode, output).Should().Be(expected);

    [Fact]
    public void The_lp_command_carries_the_options_and_never_the_file_name_as_the_title()
    {
        var arguments = LinuxPrinterService.Arguments(
            new PrintRequest(Printer, "/spool/work/print.pdf", 2, Duplex: true, Color: false, "A4", "Job 482913"));

        arguments.Should().ContainInOrder("-d", Printer, "-n", "2", "-t", "Job 482913");
        arguments.Should().Contain("sides=two-sided-long-edge")
            .And.Contain("media=A4")
            .And.Contain("ColorModel=Gray");
        arguments[^1].Should().Be("/spool/work/print.pdf");
    }

    [Fact]
    public async Task The_native_formats_come_from_the_queues_ipp_attributes()
    {
        var cups = new FakeCups
        {
            ["ipptool"] = (0,
                "\"get-printer-attributes.test\":\n    printer-name (nameWithoutLanguage) = HP_LaserJet\n" +
                "    document-format-supported (1setOf mimeMediaType) = application/octet-stream,application/pdf,application/postscript,image/jpeg\n"),
        };

        var formats = await new LinuxPrinterService(cups).GetNativeFormatsAsync(Printer);

        formats.Should().BeEquivalentTo(["application/octet-stream", PrintContentTypes.Pdf, PrintContentTypes.PostScript, PrintContentTypes.Jpeg]);
        cups.Calls.Should().ContainSingle().Which.Should().Contain("ipp://localhost/printers/HP_LaserJet");
    }

    [Fact]
    public async Task Without_ipptool_there_are_no_native_formats_and_every_job_goes_as_pdf()
    {
        var cups = new FakeCups { ["ipptool"] = (127, string.Empty) };

        (await new LinuxPrinterService(cups).GetNativeFormatsAsync(Printer)).Should().BeEmpty();
    }

    [Fact]
    public void An_original_file_is_named_to_cups_by_its_type()
        => LinuxPrinterService.Arguments(
                new PrintRequest(Printer, "/spool/work/job.ps", 1, false, false, "A4", "Job 1", PrintContentTypes.PostScript))
            .Should().Contain("document-format=application/postscript");

    [Fact]
    public async Task Discovery_reads_colour_duplex_and_A3_from_the_driver_options()
    {
        var cups = new FakeCups
        {
            ["lpstat -e"] = (0, "HP_LaserJet\nOffice_Mono\n"),
            ["lpoptions -p HP_LaserJet -l"] = (0,
                "PageSize/Media Size: *A4 A3 Letter\nDuplex/2-Sided Printing: *None DuplexNoTumble DuplexTumble\nColorModel/Color Mode: Gray *RGB\n"),
            ["lpoptions -p Office_Mono -l"] = (0, "PageSize/Media Size: *A4 Letter\nDuplex/2-Sided Printing: *None\n"),
            ["lpstat -l -p HP_LaserJet"] = (0, "printer HP_LaserJet is idle.  enabled since Mon\n\tAlerts: none"),
            ["lpstat -l -p Office_Mono"] = (0, "printer Office_Mono disabled since Mon -"),
        };

        var printers = await new LinuxPrinterService(cups).DiscoverAsync();

        printers.Should().BeEquivalentTo(new[]
        {
            new DetectedPrinter("HP_LaserJet", null, IsOnline: true, SupportsColor: true, SupportsDuplex: true, SupportsA3: true),
            new DetectedPrinter("Office_Mono", null, IsOnline: false, SupportsColor: false, SupportsDuplex: false, SupportsA3: false),
        });
    }

    [Fact]
    public async Task A_printer_without_paper_is_not_sent_the_job()
    {
        var cups = new FakeCups { [$"lpstat -l -p {Printer}"] = (0, "printer HP_LaserJet is idle.\n\tAlerts: media-empty") };

        var outcome = await new LinuxPrinterService(cups, TimeSpan.Zero).PrintAsync(Request());

        outcome.Should().Be(PrintOutcome.Failed("printer.outOfPaper"));
        cups.Calls.Should().NotContain(call => call.StartsWith("lp ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_job_that_left_the_queue_on_a_healthy_printer_is_done()
    {
        var cups = new FakeCups
        {
            [$"lpstat -l -p {Printer}"] = (0, "printer HP_LaserJet is idle.  enabled since Mon"),
            ["lp"] = (0, "request id is HP_LaserJet-17 (1 file(s))"),
            [$"lpstat -W not-completed -o {Printer}"] = (0, string.Empty),
        };

        var outcome = await new LinuxPrinterService(cups, TimeSpan.Zero).PrintAsync(Request());

        outcome.Should().Be(PrintOutcome.Done);
    }

    [Fact]
    public async Task A_jam_part_way_takes_the_job_back_so_it_cannot_restart_on_its_own()
    {
        var cups = new FakeCups
        {
            ["lp"] = (0, "request id is HP_LaserJet-18 (1 file(s))"),
            [$"lpstat -W not-completed -o {Printer}"] = (0, "HP_LaserJet-18  kiosk  1024  Mon"),
        };

        // Healthy when the job is sent, jammed once it is at the printer.
        cups.Sequence($"lpstat -l -p {Printer}",
            (0, "printer HP_LaserJet is idle.  enabled since Mon"),
            (0, "printer HP_LaserJet now printing HP_LaserJet-18.\n\tAlerts: media-jam-error"));

        var outcome = await new LinuxPrinterService(cups, TimeSpan.Zero).PrintAsync(Request());

        outcome.Should().Be(PrintOutcome.Failed("printer.paperJam"));
        cups.Calls.Should().Contain("cancel HP_LaserJet-18");
    }

    [Fact]
    public async Task The_fake_printer_is_listed_beside_the_machines_printers_and_takes_its_own_jobs()
    {
        var machine = new FakePrinterService([new DetectedPrinter(Printer, "hp", true, true, true, false)]);
        var fake = new FakePrinterService();
        var printers = new CompositePrinterService(machine, fake);

        var names = (await printers.DiscoverAsync()).Select(printer => printer.Name);
        await printers.PrintAsync(Request() with { Printer = FakePrinterService.FakePrinterName });
        await printers.PrintAsync(Request());

        names.Should().Equal(Printer, FakePrinterService.FakePrinterName);
        fake.Printed.Should().ContainSingle().Which.Printer.Should().Be(FakePrinterService.FakePrinterName);
        machine.Printed.Should().ContainSingle().Which.Printer.Should().Be(Printer);
    }

    [Fact]
    public async Task A_broken_print_system_still_leaves_the_fake_printer()
    {
        var printers = new CompositePrinterService(new BrokenPrinterService(), new FakePrinterService());

        var names = (await printers.DiscoverAsync()).Select(printer => printer.Name);

        names.Should().Equal(FakePrinterService.FakePrinterName);
    }

    private static PrintRequest Request()
        => new(Printer, Path.Combine(Path.GetTempPath(), "missing.pdf"), 1, Duplex: false, Color: false, "A4", "Job 100200");

    /// <summary>Answers CUPS commands from a table, keyed by program and arguments.</summary>
    private sealed class FakeCups : ICommandRunner
    {
        private readonly Dictionary<string, (int ExitCode, string Output)> _answers = [];
        private readonly Dictionary<string, Queue<(int ExitCode, string Output)>> _sequences = [];

        public List<string> Calls { get; } = [];

        public (int ExitCode, string Output) this[string command]
        {
            set => _answers[command] = value;
        }

        public void Sequence(string command, params (int ExitCode, string Output)[] answers)
            => _sequences[command] = new Queue<(int, string)>(answers);

        public Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var command = string.Join(' ', [program, .. arguments]);
            Calls.Add(command);

            if (_sequences.TryGetValue(command, out var sequence))
            {
                // The last answer sticks once the sequence runs out.
                return Task.FromResult(sequence.Count > 1 ? sequence.Dequeue() : sequence.Peek());
            }

            if (_answers.TryGetValue(command, out var answer) || _answers.TryGetValue(program, out answer))
            {
                return Task.FromResult(answer);
            }

            return Task.FromResult((0, string.Empty));
        }
    }

    private sealed class BrokenPrinterService : IPrinterService
    {
        public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The spooler is not running.");

        public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The spooler is not running.");

        public Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The spooler is not running.");
    }
}
