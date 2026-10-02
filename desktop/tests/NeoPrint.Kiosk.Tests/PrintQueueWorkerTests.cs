using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
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
        => new(_backend, _printer, _spool, journal ?? _journal, () => FakePrinterService.FakePrinterName, NullLogger<PrintQueueWorker>.Instance, _printer);

    private async Task<Guid> PlaceAsync(PrintQueueWorker worker, string code = "123456", int sheets = 2)
    {
        var id = Guid.NewGuid();
        await _spool.PutAsync(JournalEntry.SpoolNameFor(id), new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test")));
        worker.Accept(id, code, new JobOptions(Copies: 2), sheets, DateTime.UtcNow);
        _backend.Queue.Add(new QueueItem(id, code, _backend.Queue.Count + 1, "Queued", 1, 2, null, "a.pdf", DateTime.UtcNow));

        return id;
    }

    [Fact]
    public async Task An_original_file_reaches_the_printer_with_its_type()
    {
        var worker = Worker();
        var id = Guid.NewGuid();
        await _spool.PutAsync(JournalEntry.SpoolNameFor(id, PrintContentTypes.PostScript), new MemoryStream(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0")));
        worker.Accept(id, "654321", new JobOptions(), 1, DateTime.UtcNow, PrintContentTypes.PostScript);
        _backend.Queue.Add(new QueueItem(id, "654321", 1, "Queued", 1, 1, null, "flyer.ps", DateTime.UtcNow));

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().ContainSingle().Which.ContentType.Should().Be(PrintContentTypes.PostScript);
        _spool.Exists(JournalEntry.SpoolNameFor(id, PrintContentTypes.PostScript)).Should().BeFalse();
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

    /// <summary>The kiosk wrote "Printing", handed the job to the spooler, and then the power went.</summary>
    private async Task<Guid> DiedWhilePrintingAsync(PageCount? counterBefore = null)
    {
        var id = await PlaceAsync(Worker(), sheets: 2);
        _journal.Save(_journal.Find(id)! with
        {
            State = JournalState.Printing,
            Attempts = 1,
            Printer = FakePrinterService.FakePrinterName,
            SpoolerJobId = "NeoPrint-123456",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CounterBefore = counterBefore,
            Sides = 4,
        });

        return id;
    }

    [Fact]
    public async Task A_job_still_in_the_spooler_after_a_restart_is_watched_and_nothing_else_starts()
    {
        var id = await DiedWhilePrintingAsync();
        var second = await PlaceAsync(Worker(), code: "222222");
        _printer.SpoolerStates["NeoPrint-123456"] = SpoolerJobState.Pending;

        var restarted = Worker(new JobJournal(_paths));
        await restarted.RecoverAsync(CancellationToken.None);
        await restarted.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty("the printer still has the job from before the restart");
        new JobJournal(_paths).Find(id)!.State.Should().Be(JournalState.Printing);

        _printer.SpoolerStates["NeoPrint-123456"] = SpoolerJobState.Completed;
        await restarted.StepAsync(CancellationToken.None);

        _backend.Reports.Should().Contain((id, "Completed", 4, 2, (string?)null));
        _backend.Attempts[_backend.Reports.FindIndex(report => report.JobId == id && report.Status == "Completed")]!.Recovered.Should().BeTrue();
        _printer.Printed.Should().ContainSingle().Which.JobName.Should().Be("NeoPrint-222222", "the next job goes once the printer is free");
        second.Should().NotBe(id);
    }

    [Fact]
    public async Task A_job_the_spooler_has_forgotten_is_completed_when_the_counter_shows_every_sheet()
    {
        var id = await DiedWhilePrintingAsync(new PageCount(1000, CountsSheets: true));
        _printer.Counter = new PageCount(1002, CountsSheets: true);

        var restarted = Worker(new JobJournal(_paths));
        await restarted.RecoverAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty();
        _backend.Reports.Should().ContainSingle(report => report.JobId == id && report.Status == "Completed" && report.Sheets == 2);

        var attempt = _backend.Attempts.Single()!;
        attempt.CounterBefore.Should().Be(1000);
        attempt.CounterAfter.Should().Be(1002);
        attempt.CounterUnit.Should().Be("sheets");
    }

    [Fact]
    public async Task A_forgotten_job_with_a_counter_short_of_it_is_interrupted_with_what_came_out()
    {
        // Impressions: 4 sides wanted, 2 came out - one duplex sheet of two.
        var id = await DiedWhilePrintingAsync(new PageCount(500, CountsSheets: false));
        _journal.Save(_journal.Find(id)! with { Options = new JobOptions(Copies: 2, Duplex: true) });
        _printer.Counter = new PageCount(502, CountsSheets: false);

        var restarted = Worker(new JobJournal(_paths));
        await restarted.RecoverAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty("never printed again");
        _backend.Reports.Should().ContainSingle(report => report.JobId == id && report.Status == "Interrupted"
                                                            && report.Sheets == 1 && report.ErrorCode == "printer.restarted");
    }

    [Fact]
    public async Task Every_attempt_carries_the_printer_the_spooler_reference_and_the_counter()
    {
        _printer.Counter = new PageCount(70, CountsSheets: true);
        var worker = Worker();
        var id = await PlaceAsync(worker);

        await worker.StepAsync(CancellationToken.None);

        var completed = _backend.Attempts[_backend.Reports.FindIndex(report => report.JobId == id && report.Status == "Completed")]!;
        completed.Number.Should().Be(1);
        completed.Printer.Should().Be(FakePrinterService.FakePrinterName);
        completed.SpoolerJobId.Should().Be("NeoPrint-123456");
        completed.CounterBefore.Should().Be(70);
        completed.Recovered.Should().BeFalse();
        completed.FinishedAt.Should().NotBeNull();
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
    public async Task The_fake_printer_can_break_the_next_job_once_to_walk_the_refund_path_by_hand()
    {
        // T-123: printer-state.txt only makes a job wait; printer-fail-next.txt
        // breaks a job at the printer, which is what a refund follows (scenario A).
        var worker = Worker();
        var broken = await PlaceAsync(worker, "111111");
        var failNext = Path.Combine(_paths.SpoolDirectory, "printed", "printer-fail-next.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(failNext)!);
        await File.WriteAllTextAsync(failNext, "PaperJam");

        await worker.StepAsync(CancellationToken.None);

        _backend.Reports[^1].Should().Be((broken, "Interrupted", 0, 0, "printer.paperJam"));
        File.Exists(failNext).Should().BeFalse("it breaks one job, not every one after it");

        _backend.Queue.RemoveAll(item => item.JobId == broken);
        await PlaceAsync(worker, "222222");
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Select(print => print.JobName).Should().Equal("NeoPrint-222222");
    }

    [Fact]
    public async Task A_job_waiting_for_the_printer_waits_for_the_tick_and_does_not_flood_the_backend()
    {
        // T-121: a jammed printer made the worker go round at once, asking for the
        // queue without a pause until the backend rate-limited the kiosk (429) and
        // its heartbeats stopped reaching it.
        var worker = Worker();
        await PlaceAsync(worker);
        _printer.SimulatedState = PrinterState.PaperJam;

        using var run = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await worker.RunAsync(run.Token);

        _printer.Printed.Should().BeEmpty();
        _backend.QueueReads.Should().BeLessThanOrEqualTo(2, "the next look is on the poll interval, not at once");
    }

    [Fact]
    public async Task A_job_cancelled_on_the_backend_loses_its_file_and_does_not_print()
    {
        var worker = Worker();
        var id = await PlaceAsync(worker);
        _backend.Queue.Clear();

        // Just queued: its payment may not be visible yet, so the file stays a while.
        await worker.StepAsync(CancellationToken.None);
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeTrue();

        Age(id, TimeSpan.FromMinutes(3));
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty();
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeFalse();
        _journal.Find(id).Should().BeNull();
    }

    [Fact]
    public async Task An_unpaid_order_never_prints_not_even_without_the_backend_and_keeps_its_file()
    {
        var worker = Worker();
        var id = await PlaceUnpaidAsync(worker);

        await worker.StepAsync(CancellationToken.None);
        _backend.Down = true;
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().BeEmpty("printing from the journal without the backend must never be a free print");
        _journal.Find(id)!.State.Should().Be(JournalState.AwaitingPayment);
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeTrue("the payment may still come");
    }

    [Fact]
    public async Task A_payment_seen_on_the_screen_or_in_the_backend_s_queue_lets_the_order_print()
    {
        var worker = Worker();

        var confirmed = await PlaceUnpaidAsync(worker, "111111");
        worker.MarkPaid(confirmed);
        _backend.Queue.Add(new QueueItem(confirmed, "111111", 1, "Queued", 1, 1, null, "a.pdf", DateTime.UtcNow));
        await worker.StepAsync(CancellationToken.None);

        // Paid while the kiosk was not looking: the queue says so.
        _backend.Queue.Clear();
        var quiet = await PlaceUnpaidAsync(worker, "222222");
        _backend.Queue.Add(new QueueItem(quiet, "222222", 1, "Queued", 1, 1, null, "b.pdf", DateTime.UtcNow));
        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Select(print => print.JobName).Should().Equal("NeoPrint-111111", "NeoPrint-222222");
    }

    [Fact]
    public async Task An_order_never_paid_loses_its_file_after_a_while()
    {
        var worker = Worker();
        var id = await PlaceUnpaidAsync(worker);

        _journal.Save(_journal.Find(id)! with { QueuedAt = DateTime.UtcNow.AddHours(-3) });
        await worker.StepAsync(CancellationToken.None);

        _journal.Find(id).Should().BeNull();
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeFalse();
    }

    private async Task<Guid> PlaceUnpaidAsync(PrintQueueWorker worker, string code = "123456")
    {
        var id = Guid.NewGuid();
        await _spool.PutAsync(JournalEntry.SpoolNameFor(id), new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test")));
        worker.Accept(id, code, new JobOptions(), 1, DateTime.UtcNow, paid: false);

        return id;
    }

    private void Age(Guid id, TimeSpan by) => _journal.Save(_journal.Find(id)! with { UpdatedAt = DateTime.UtcNow - by });

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

    [Fact]
    public async Task A_paid_order_from_the_app_is_fetched_into_the_spool_and_printed()
    {
        var remote = new FakeRemoteOrders(_spool);
        var worker = new PrintQueueWorker(
            _backend, _printer, _spool, _journal, () => FakePrinterService.FakePrinterName, NullLogger<PrintQueueWorker>.Instance, _printer, remote);
        var id = Guid.NewGuid();
        remote.Orders[id] = new RemoteOrder(id, "777001", new JobOptions(Copies: 1), 3, 3);
        _backend.Queue.Add(new QueueItem(id, "777001", 1, "Queued", 3, 1, null, "hesabat.pdf", DateTime.UtcNow));

        await worker.StepAsync(CancellationToken.None);

        remote.Fetched.Should().Equal(id);
        _printer.Printed.Should().ContainSingle().Which.JobName.Should().Be("NeoPrint-777001");
        _backend.Reports.Should().Equal((id, "Printing", 0, 0, (string?)null), (id, "Completed", 3, 3, (string?)null));
        _spool.Exists(JournalEntry.SpoolNameFor(id)).Should().BeFalse();

        // The backend takes a printed job off its queue; the kiosk forgets it.
        _backend.Queue.RemoveAll(item => item.JobId == id);
        await worker.StepAsync(CancellationToken.None);
        remote.Fetched.Should().ContainSingle();
    }

    [Fact]
    public async Task An_order_whose_file_does_not_come_waits_while_the_next_one_prints()
    {
        var remote = new FakeRemoteOrders(_spool) { Failing = true };
        var worker = new PrintQueueWorker(
            _backend, _printer, _spool, _journal, () => FakePrinterService.FakePrinterName, NullLogger<PrintQueueWorker>.Instance, _printer, remote);
        var stuck = Guid.NewGuid();
        remote.Orders[stuck] = new RemoteOrder(stuck, "777002", new JobOptions(), 1, 1);
        _backend.Queue.Add(new QueueItem(stuck, "777002", 1, "Queued", 1, 1, null, "a.pdf", DateTime.UtcNow));
        var flash = await PlaceAsync(worker, code: "777003");

        await worker.StepAsync(CancellationToken.None);

        _printer.Printed.Should().ContainSingle().Which.JobName.Should().Be("NeoPrint-777003", "the one with its file here goes on");
        _journal.Find(stuck).Should().BeNull("nothing is recorded for an order with no file");
        _backend.Reports.Should().NotContain(report => report.JobId == stuck);

        // Asked again only after a pause, not on every round.
        _backend.Queue.RemoveAll(item => item.JobId == flash);
        await worker.StepAsync(CancellationToken.None);
        remote.Attempts.Should().Be(1);
    }

    /// <summary>An app whose orders are fetched from a dictionary instead of the backend.</summary>
    private sealed class FakeRemoteOrders : IRemoteOrders
    {
        private readonly SpoolStore _spool;

        public FakeRemoteOrders(SpoolStore spool) => _spool = spool;

        public Dictionary<Guid, RemoteOrder> Orders { get; } = [];

        public List<Guid> Fetched { get; } = [];

        public bool Failing { get; set; }

        public int Attempts { get; private set; }

        public async Task<RemoteOrder?> FetchAsync(Guid jobId, CancellationToken cancellationToken)
        {
            Attempts++;

            if (Failing)
            {
                throw new HttpRequestException("the file did not come");
            }

            if (!Orders.TryGetValue(jobId, out var order))
            {
                return null;
            }

            await _spool.PutAsync(JournalEntry.SpoolNameFor(jobId), new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 app")), cancellationToken);
            Fetched.Add(jobId);
            return order;
        }
    }

    private sealed class FakeBackend : IPrintBackend
    {
        public List<QueueItem> Queue { get; } = [];

        public List<(Guid JobId, string Status, int Sides, int Sheets, string? ErrorCode)> Reports { get; } = [];

        public List<PrintAttemptReport?> Attempts { get; } = [];

        public bool Down { get; set; }

        public int QueueReads { get; private set; }

        public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken)
        {
            QueueReads++;
            return Down ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<QueueItem>>([.. Queue]);
        }

        public Task ReportAsync(
            Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, PrintAttemptReport? attempt, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("down");
            }

            Reports.Add((jobId, status, printedSides, printedSheets, errorCode));
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }
    }
}
