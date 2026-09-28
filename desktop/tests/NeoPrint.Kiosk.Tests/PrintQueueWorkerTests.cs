using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's print queue: one job at a time, the journal written before the
/// printer is touched, and no job printed twice (plan section 7.4; step 3 DoD).
/// </summary>
public sealed class PrintQueueWorkerTests : IDisposable
{
    private readonly FakeAppPaths _paths = new();
    private readonly FakeBackend _backend = new();
    private readonly FakePrinterService _printer;
    private readonly SpoolStore _spool;
    private readonly JobJournal _journal;

    public PrintQueueWorkerTests()
    {
        _paths.EnsureCreated();
        _printer = new FakePrinterService(outputDirectory: Path.Combine(_paths.SpoolDirectory, "printed"));
        _spool = new SpoolStore(_paths, new FakeSpoolKeyStore(_paths));
        _journal = new JobJournal(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    private PrintQueueWorker Worker(JobJournal? journal = null)
        => new(_backend, _printer, _spool, journal ?? _journal, () => FakePrinterService.FakePrinterName, NullLogger<PrintQueueWorker>.Instance);

    private async Task<Guid> PlaceAsync(PrintQueueWorker worker, string code = "123456", int sheets = 2)
    {
        var id = Guid.NewGuid();
        await _spool.PutAsync(JournalEntry.SpoolNameFor(id), new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test")));
        worker.Accept(id, code, new JobOptions(Copies: 2), sheets, DateTime.UtcNow);
        _backend.Queue.Add(new QueueItem(id, code, _backend.Queue.Count + 1, "Queued", 1, 2, null, "a.pdf", DateTime.UtcNow));

        return id;
    }

    [Fact]
    public async Task A_queued_job_is_printed_reported_and_forgotten()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker, sheets: 2);

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().ContainSingle().Which.JobName.Should().Be("NeoPrint-123456");
        _printer.Printed[0].Copies.Should().Be(2);
        _backend.Reports.Should().Equal(
            (id, "Printing", 0, 0, (string?)null),
            (id, "Completed", 2, 2, (string?)null));

        _journal.Find(id).Should().BeNull("a job that is printed and reported is done");
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeFalse("the print file goes once it is printed");
        (Directory.Exists(_spool.WorkFolder) && Directory.EnumerateFileSystemEntries(_spool.WorkFolder).Any())
            .Should().BeFalse("no decrypted copy is left behind");
    }

    [Fact]
    public async Task The_spool_holds_ciphertext_only()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);

        var stored = Directory.GetFiles(Path.Combine(_paths.SpoolDirectory, "jobs"));
        var bytes = await File.ReadAllBytesAsync(stored.Single());

        Encoding.ASCII.GetString(bytes).Should().NotContain("%PDF", "the spool is encrypted (plan section 7.5)");
        _journal.Find(id)!.State.Should().Be(JournalState.Queued);
    }

    [Fact]
    public async Task A_job_at_the_printer_when_the_kiosk_died_is_never_printed_again()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);

        // The kiosk wrote "Printing" and then the power went.
        _journal.Save(_journal.Find(id)! with { State = JournalState.Printing, Attempts = 1 });

        var restarted = Worker(new JobJournal(_paths));
        await restarted.RecoverAsync(CancellationToken.None);
        await restarted.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty("nobody knows what came out, so nothing is sent again");
        _backend.Reports.Should().ContainSingle(report => report.JobId == id && report.Status == "Interrupted"
                                                            && report.ErrorCode == "printer.restarted");
    }

    [Fact]
    public async Task Without_the_backend_the_queue_still_prints_and_the_reports_wait()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);
        _backend.Down = true;

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().ContainSingle("a paid queue is worked through in Degraded mode (plan 7.3)");
        _backend.Reports.Should().BeEmpty();
        _journal.Find(id)!.State.Should().Be(JournalState.Printed);

        _backend.Down = false;
        _backend.Queue.Clear();
        await worker.StepAsync(CancellationToken.None);

        _backend.Reports.Should().ContainSingle(report => report.JobId == id && report.Status == "Completed");
        _journal.Find(id).Should().BeNull();
    }

    [Fact]
    public async Task A_printer_out_of_paper_leaves_the_job_waiting_rather_than_failing_it()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);
        _printer.SimulatedState = PrinterState.OutOfPaper;

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty();
        _backend.Reports.Should().BeEmpty();
        _journal.Find(id)!.State.Should().Be(JournalState.Queued);
        worker.Activity.LastErrorCode.Should().Be("printer.outOfPaper");

        _printer.SimulatedState = null;
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_job_cancelled_on_the_backend_loses_its_file_and_does_not_print()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);
        _backend.Queue.Clear();

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty();
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeFalse();
        _journal.Find(id).Should().BeNull();
    }

    [Fact]
    public async Task Jobs_print_in_the_backend_s_order_one_at_a_time()
    {
        var worker = Worker();
        var first = await PlaceAsync(worker, "111111");
        var second = await PlaceAsync(worker, "222222");

        await worker.StepAsync(CancellationToken.None);
        _backend.Queue.RemoveAll(item => item.JobId == first);
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Select(print => print.JobName).Should().Equal("NeoPrint-111111", "NeoPrint-222222");
        _journal.Find(second).Should().BeNull();
    }

    [Fact]
    public async Task A_paused_kiosk_takes_no_new_job()
    {
        var worker = Worker();
        await PlaceAsync(worker);
        worker.Paused = true;

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty();
    }

    private sealed class FakeBackend : IPrintBackend
    {
        public List<QueueItem> Queue { get; } = [];

        public List<(Guid JobId, string Status, int Sides, int Sheets, string? ErrorCode)> Reports { get; } = [];

        public bool Down { get; set; }

        public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken)
            => Down ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<QueueItem>>([.. Queue]);

        public Task ReportAsync(Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("down");
            }

            Reports.Add((jobId, status, printedSides, printedSheets, errorCode));
            return Task.CompletedTask;
        }
    }
}
