using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The page a job is on and when the queue should be ready (T-360): screen
/// only - nothing is reported or charged by it.
/// </summary>
public sealed class PrintProgressTests : IDisposable
{
    private readonly FakeAppPaths _paths = new();

    public PrintProgressTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public void The_pace_starts_at_a_small_printer_and_learns_a_third_of_the_way_from_each_job()
    {
        var pace = new PrintPace();
        pace.SecondsPerSide.Should().Be(PrintPace.DefaultSecondsPerSide);

        // 10 pages in 8 s of start + 100 s: 10 s a page.
        pace.Learn(10, TimeSpan.FromSeconds(PrintPace.JobOverheadSeconds + 100));

        pace.SecondsPerSide.Should().BeApproximately(4 + (10 - 4) / 3.0, 0.001);
    }

    [Fact]
    public void Nothing_learnt_from_an_empty_job_and_a_jam_cannot_make_a_page_last_minutes()
    {
        var pace = new PrintPace();
        pace.Learn(0, TimeSpan.FromSeconds(30));
        pace.Learn(5, TimeSpan.Zero);
        pace.SecondsPerSide.Should().Be(PrintPace.DefaultSecondsPerSide);

        pace.Learn(1, TimeSpan.FromHours(1));
        pace.SecondsPerSide.Should().BeLessThanOrEqualTo(4 + (60 - 4) / 3.0 + 0.001);
    }

    [Fact]
    public void By_the_clock_the_last_page_is_never_claimed()
    {
        var pace = new PrintPace();

        pace.SidesByTime(7, TimeSpan.FromSeconds(PrintPace.JobOverheadSeconds + 12)).Should().Be(3);
        pace.SidesByTime(7, TimeSpan.FromMinutes(10)).Should().Be(6);
        pace.SidesByTime(7, TimeSpan.Zero).Should().Be(0);
    }

    [Fact]
    public void Each_job_is_ready_after_the_one_before_it()
    {
        var pace = new PrintPace();
        var now = new DateTime(2026, 11, 9, 14, 0, 0);

        var finishes = pace.Finishes(
            [new QueuedWork(7, 3, Printing: true), new QueuedWork(10, 0, Printing: false)], now);

        // 4 pages left x 4 s; then 8 s to start and 10 x 4 s.
        finishes[0].Should().Be(now.AddSeconds(16));
        finishes[1].Should().Be(now.AddSeconds(16 + 8 + 40));
    }

    [Fact]
    public async Task While_a_job_prints_its_pages_are_followed_by_the_printers_counter()
    {
        var printer = new HeldPrinter();
        var spool = new SpoolStore(_paths, new FakeSpoolKeyStore(_paths));
        var backend = new QuietBackend();
        var worker = new PrintQueueWorker(
            backend, printer, spool, new JobJournal(_paths), () => HeldPrinter.Name, NullLogger<PrintQueueWorker>.Instance, printer)
        {
            ProgressInterval = TimeSpan.FromMilliseconds(20),
        };
        var seen = new List<PrinterActivity>();
        worker.ActivityChanged += activity =>
        {
            lock (seen)
            {
                seen.Add(activity);
            }
        };

        var id = Guid.NewGuid();
        await spool.PutAsync(JournalEntry.SpoolNameFor(id), new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 test")));
        worker.Accept(id, "360001", new JobOptions(), 7, DateTime.UtcNow, sides: 7);
        backend.Queue.Add(new QueueItem(id, "360001", 1, "Queued", 7, 1, null, "a.pdf", DateTime.UtcNow));

        var step = worker.StepAsync(CancellationToken.None);
        await printer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        printer.Counter = new PageCount(1003, CountsSheets: false);
        await WaitForAsync(() => Has(seen, 3));
        // The counter says all seven, but the last page is the printer's to say.
        printer.Counter = new PageCount(1007, CountsSheets: false);
        await WaitForAsync(() => Has(seen, 6));

        printer.Finish.SetResult(PrintOutcome.Done);
        await step.WaitAsync(TimeSpan.FromSeconds(10));

        lock (seen)
        {
            seen.Should().Contain(activity => activity.JobCode == "360001" && activity.PrintedSides == 3 && activity.TotalSides == 7);
            seen.Should().NotContain(activity => activity.PrintedSides >= 7);
            seen[^1].JobId.Should().BeNull("the job left the printer");
        }

        worker.Pace.SecondsPerSide.Should().BeLessThan(PrintPace.DefaultSecondsPerSide, "a fast job teaches a faster pace");
    }

    [Fact]
    public void The_queue_panel_shows_the_page_the_job_is_on_and_when_each_will_be_ready()
    {
        var model = new MainWindowViewModel { Pace = new PrintPace() };
        model.SetQueue(
        [
            new QueueEntry(1, "360001", string.Empty, 7, IsPrinting: true) { Sides = 7 },
            new QueueEntry(2, "360002", string.Empty, 10, IsPrinting: false) { Sides = 10 },
        ]);

        model.SetPrintProgress("360001", 3, 7);

        var printing = model.Queue[0];
        printing.ShowProgress.Should().BeTrue();
        printing.ProgressText.Should().Be("3 / 7 səhifə");
        printing.ProgressPercent.Should().BeApproximately(100.0 * 3 / 7, 0.001);
        printing.FinishText.Should().MatchRegex(@"^Hazır ≈ \d\d:\d\d$");

        var waiting = model.Queue[1];
        waiting.ShowProgress.Should().BeFalse();
        waiting.ProgressText.Should().BeEmpty();
        waiting.FinishText.Should().StartWith("Hazır ≈ ");
    }

    [Fact]
    public void Without_a_pace_there_are_no_times_and_without_progress_the_job_starts_at_page_zero()
    {
        var model = new MainWindowViewModel();
        model.SetQueue([new QueueEntry(1, "360003", string.Empty, 4, IsPrinting: true) { Sides = 4 }]);

        model.Queue[0].FinishText.Should().BeEmpty();
        model.Queue[0].ProgressText.Should().Be("0 / 4 səhifə");
    }

    private static bool Has(List<PrinterActivity> seen, int sides)
    {
        lock (seen)
        {
            return seen.Any(activity => activity.PrintedSides == sides);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(until, "the progress should have been seen by now");
            await Task.Delay(10);
        }
    }

    /// <summary>A printer that holds the job until the test lets it finish, with a counter the test moves.</summary>
    private sealed class HeldPrinter : IPrinterService, IPrinterPageCounter
    {
        public const string Name = "Held";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PrintOutcome> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PageCount? Counter { get; set; } = new(1000, CountsSheets: false);

        public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedPrinter>>([]);

        public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
            => Task.FromResult(PrinterState.Online);

        public async Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return await Finish.Task.WaitAsync(cancellationToken);
        }

        public Task<PageCount?> ReadAsync(string printer, CancellationToken cancellationToken = default) => Task.FromResult(Counter);
    }

    private sealed class QuietBackend : IPrintBackend
    {
        public List<QueueItem> Queue { get; } = [];

        public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<QueueItem>>([.. Queue]);

        public Task ReportAsync(
            Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, PrintAttemptReport? attempt, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
