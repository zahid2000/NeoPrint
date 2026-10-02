using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>What the queue worker needs from the backend; <see cref="KioskApiClient"/> in the application.</summary>
public interface IPrintBackend
{
    Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken);

    Task ReportAsync(
        Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, PrintAttemptReport? attempt, CancellationToken cancellationToken);
}

/// <summary>
/// Orders placed somewhere other than this kiosk's screen - the app in a
/// session (plan section 9.3) - whose print file the kiosk fetches once they
/// are paid (plan section 7.4: backend → encrypted spool → printer).
/// </summary>
public interface IRemoteOrders
{
    /// <summary>
    /// Puts the order's print file, prepared with its options, into the spool
    /// under <see cref="JournalEntry.SpoolNameFor"/>. Null when it is not an
    /// order to fetch (a flash order's file is placed here, never fetched).
    /// </summary>
    Task<RemoteOrder?> FetchAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>What the journal needs to know of a fetched order.</summary>
public sealed record RemoteOrder(Guid JobId, string JobCode, JobOptions Options, int RequiredSheets, int Sides);

/// <summary>The backend calls of <see cref="PrintQueueWorker"/>, over the kiosk's API client.</summary>
public sealed class ApiPrintBackend : IPrintBackend
{
    private readonly KioskApiClient _api;

    public ApiPrintBackend(KioskApiClient api) => _api = api;

    public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken) => _api.GetQueueAsync(cancellationToken);

    public Task ReportAsync(
        Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, PrintAttemptReport? attempt, CancellationToken cancellationToken)
        => _api.ReportPrintJobStatusAsync(jobId, status, printedSides, printedSheets, errorCode, cancellationToken, attempt);
}

/// <summary>What the worker is doing, for the screen.</summary>
public sealed record PrinterActivity(Guid? JobId, string? JobCode, PrinterState? PrinterState, string? LastErrorCode);

/// <summary>
/// Prints this kiosk's queue, one job at a time (plan section 7.4).
/// </summary>
/// <remarks>
/// <para>
/// Woken by <c>QueueUpdated</c> and <c>JobAdded</c> from the hub, and by a
/// timer in case one was missed. It takes the first queued job whose print
/// file is in the spool, marks it Printing in the journal before anything
/// reaches the printer, prints it, and reports the outcome.
/// </para>
/// <para>
/// When the backend cannot be reached, it keeps printing what the journal
/// says is queued and paid - the Degraded mode of plan section 7.3 - and the
/// reports wait in the journal until the link is back.
/// </para>
/// </remarks>
public sealed class PrintQueueWorker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long an unpaid order's file is kept: well past the longest payment timeout (30 minutes).</summary>
    private static readonly TimeSpan UnpaidLifetime = TimeSpan.FromHours(2);

    /// <summary>How long to wait before trying again to fetch an order whose file did not come.</summary>
    private static readonly TimeSpan FetchRetry = TimeSpan.FromSeconds(30);

    /// <summary>How long a queued job may be missing from the backend's queue before its file goes.</summary>
    private static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(2);

    private readonly IPrintBackend _backend;
    private readonly IPrinterService _printers;
    private readonly SpoolStore _spool;
    private readonly JobJournal _journal;
    private readonly Func<string?> _selectedPrinter;
    private readonly IPrinterPageCounter? _counter;
    private readonly IRemoteOrders? _remote;

    /// <summary>When an order could not be fetched, so it is tried again after a pause rather than on every round.</summary>
    private readonly Dictionary<Guid, DateTime> _fetchFailedAt = [];
    private readonly ILogger<PrintQueueWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _printerLock = new(1, 1);

    public PrintQueueWorker(
        IPrintBackend backend,
        IPrinterService printers,
        SpoolStore spool,
        JobJournal journal,
        Func<string?> selectedPrinter,
        ILogger<PrintQueueWorker> logger,
        IPrinterPageCounter? counter = null,
        IRemoteOrders? remote = null)
    {
        _remote = remote;
        _backend = backend;
        _printers = printers;
        _spool = spool;
        _journal = journal;
        _selectedPrinter = selectedPrinter;
        _logger = logger;
        _counter = counter;
    }

    /// <summary>Stops taking new jobs (the kiosk is paused or out of service); the one at the printer finishes.</summary>
    public bool Paused { get; set; }

    /// <summary>What is at the printer now, and how the printer was last seen.</summary>
    public PrinterActivity Activity { get; private set; } = new(null, null, null, null);

    /// <summary>Raised when <see cref="Activity"/> changes.</summary>
    public event Action<PrinterActivity>? ActivityChanged;

    /// <summary>A job left the printer, one way or the other.</summary>
    public event Action<JournalEntry>? JobFinished;

    /// <summary>Something changed: look at the queue now rather than at the next tick.</summary>
    public void Wake() => _wake.Release();

    /// <summary>
    /// A job placed at this kiosk: its print file is already in the spool.
    /// Recorded first, so the worker knows its options even without the backend.
    /// </summary>
    /// <param name="contentType">What was spooled: null for the prepared PDF, or the original's type.</param>
    /// <param name="sides">Printed sides the job takes; the printer's counter is checked against it after a restart.</param>
    /// <param name="paid">False for an order still to be paid: it waits until <see cref="MarkPaid"/> or the backend's queue says it is paid.</param>
    public void Accept(
        Guid jobId, string jobCode, JobOptions options, int requiredSheets, DateTime queuedAt, string? contentType = null, int sides = 0, bool paid = true)
    {
        _journal.Save(new JournalEntry(
            jobId, jobCode, paid ? JournalState.Queued : JournalState.AwaitingPayment, options, requiredSheets, 0, 0, null, false, queuedAt,
            DateTime.UtcNow, contentType, sides));
        Wake();
    }

    /// <summary>The payment went through (<c>PaymentConfirmed</c>, or the payment screen saw it): the job may print.</summary>
    public void MarkPaid(Guid jobId)
    {
        if (_journal.Find(jobId) is { State: JournalState.AwaitingPayment } entry)
        {
            _journal.Save(entry with { State = JournalState.Queued, UpdatedAt = DateTime.UtcNow });
            Wake();
        }
    }

    /// <summary>
    /// After a restart: a job that was at the printer is reported
    /// interrupted, never printed again; reports that did not reach the
    /// backend are sent now (plan step 3 DoD: no double print).
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        _spool.ClearWork();

        await ResolveUnfinishedAsync(cancellationToken).ConfigureAwait(false);
        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out what became of the jobs that were at the printer when the
    /// kiosk stopped (plan section 7.12, step 4): still in the spooler -
    /// watched until it leaves; printed - Completed; gone or failed - the
    /// printer's counter decides between Completed and Interrupted. Never
    /// printed again.
    /// </summary>
    /// <returns>Whether one is still at the printer, which then waits for it.</returns>
    private async Task<bool> ResolveUnfinishedAsync(CancellationToken cancellationToken)
    {
        var busy = false;

        foreach (var entry in _journal.All().Where(entry => entry.State == JournalState.Printing))
        {
            var spooler = await SpoolerStateAsync(entry, cancellationToken).ConfigureAwait(false);

            if (spooler == SpoolerJobState.Pending)
            {
                _logger.LogInformation("Job {JobCode} is still with the printer after the restart; watching it.", entry.JobCode);
                busy = true;
                continue;
            }

            var after = await ReadCounterAsync(entry.Printer, cancellationToken).ConfigureAwait(false);
            var printedSheets = spooler == SpoolerJobState.Completed ? entry.RequiredSheets : SheetsByCounter(entry, after);
            var printed = printedSheets >= entry.RequiredSheets;

            _logger.LogWarning(
                "Job {JobCode} was at the printer when the kiosk stopped; the spooler says {Spooler}, the counter {Sheets} sheet(s): {Outcome}. It is not printed again.",
                entry.JobCode,
                spooler,
                printedSheets,
                printed ? "printed" : "interrupted");

            _journal.Save(entry with
            {
                State = printed ? JournalState.Printed : JournalState.Interrupted,
                PrintedSheets = Math.Min(printedSheets, entry.RequiredSheets),
                ErrorCode = printed ? null : "printer.restarted",
                StatusReported = false,
                CounterAfter = after,
                Recovered = true,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        return busy;
    }

    private async Task<SpoolerJobState> SpoolerStateAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (entry.SpoolerJobId is null || entry.Printer is null || _printers is not IPrintJobTracker tracker)
        {
            // Never submitted, or nothing to ask: the counter is all there is.
            return SpoolerJobState.NotFound;
        }

        try
        {
            return await tracker.GetJobStateAsync(entry.Printer, entry.SpoolerJobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The spooler could not be asked about job {JobCode}.", entry.JobCode);
            return SpoolerJobState.NotFound;
        }
    }

    /// <summary>
    /// What the printer's counter says came out between the attempt's start
    /// and now; 0 when there is no counter, so an unknown outcome is never
    /// taken for a printed job.
    /// </summary>
    private static int SheetsByCounter(JournalEntry entry, PageCount? after)
    {
        if (entry.CounterBefore is not { } before || after is null || before.CountsSheets != after.CountsSheets)
        {
            return 0;
        }

        var delta = Math.Max(0, after.Value - before.Value);

        if (before.CountsSheets)
        {
            return (int)Math.Min(delta, int.MaxValue);
        }

        // Impressions: every side the job takes has to be there.
        var sides = entry.Sides > 0 ? entry.Sides : entry.RequiredSheets * (entry.Options.Duplex ? 2 : 1);

        return delta >= sides ? entry.RequiredSheets : (int)(delta / (entry.Options.Duplex ? 2 : 1));
    }

    private async Task<PageCount?> ReadCounterAsync(string? printer, CancellationToken cancellationToken)
    {
        if (_counter is null || printer is null)
        {
            return null;
        }

        try
        {
            return await _counter.ReadAsync(printer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The page counter of {Printer} could not be read.", printer);
            return null;
        }
    }

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (DeviceRevokedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The print queue step failed; trying again.");
            }

            try
            {
                await _wake.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the printer to itself: the test page
    /// waits for the job in progress and goes before the next (plan 7.7).
    /// </summary>
    public async Task<T> WithPrinterAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _printerLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _printerLock.Release();
        }
    }

    /// <summary>One pass: send what is owed, sync with the queue, print the next job.</summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);

        if (await ResolveUnfinishedAsync(cancellationToken).ConfigureAwait(false))
        {
            // The printer still has the job from before the restart.
            return;
        }

        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);

        var next = await NextAsync(cancellationToken).ConfigureAwait(false);

        if (next is null || Paused)
        {
            return;
        }

        await _printerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool sent;

        try
        {
            sent = await PrintAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _printerLock.Release();
        }

        // There may be more; do not wait for the tick. A job that waits for the
        // printer waits for the tick too: going round at once would ask the
        // backend for the queue without a pause and use up the kiosk's rate limit.
        if (sent)
        {
            Wake();
        }
    }

    private async Task<JournalEntry?> NextAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueItem>? queue = null;

        try
        {
            queue = await _backend.GetQueueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The queue could not be read from the backend; printing from the journal.");
        }

        if (queue is not null)
        {
            // A job the backend no longer has in the queue was cancelled (or
            // finished elsewhere): its print file goes.
            var live = queue.Select(item => item.JobId).ToHashSet();

            // A job just marked paid may not be in the queue the backend answered
            // with yet (its confirmation can arrive before the payment's
            // transaction is visible): it gets a grace period, or a paid order
            // would lose its file.
            foreach (var stale in _journal.All().Where(entry => entry.State == JournalState.Queued
                                                                && !live.Contains(entry.JobId)
                                                                && DateTime.UtcNow - entry.UpdatedAt > StaleGrace))
            {
                _logger.LogInformation("Job {JobCode} left the queue before it printed; removing its file.", stale.JobCode);
                _spool.Delete(stale.SpoolName);
                _journal.Remove(stale.JobId);
            }

            // Paid while nobody here was looking (the kiosk restarted, the event was
            // lost): the backend's queue has it, so it may print now.
            foreach (var item in queue.Where(item => item.Status == "Queued"))
            {
                if (_journal.Find(item.JobId) is { State: JournalState.AwaitingPayment } unpaid)
                {
                    _journal.Save(unpaid with { State = JournalState.Queued, UpdatedAt = DateTime.UtcNow });
                }
            }

            // Never paid: the intent ran out long ago and the order with it.
            foreach (var abandoned in _journal.All()
                         .Where(entry => entry.State == JournalState.AwaitingPayment
                                         && !live.Contains(entry.JobId)
                                         && DateTime.UtcNow - entry.QueuedAt > UnpaidLifetime))
            {
                _logger.LogInformation("Job {JobCode} was never paid; removing its file.", abandoned.JobCode);
                _spool.Delete(abandoned.SpoolName);
                _journal.Remove(abandoned.JobId);
            }

            await FetchRemoteAsync(queue, cancellationToken).ConfigureAwait(false);

            // In the backend's order: the first queued job whose file is here.
            foreach (var item in queue.Where(item => item.Status == "Queued"))
            {
                if (_journal.Find(item.JobId) is { State: JournalState.Queued } entry && _spool.Exists(entry.SpoolName))
                {
                    return entry;
                }
            }

            return null;
        }

        return _journal.All()
            .Where(entry => entry.State == JournalState.Queued && _spool.Exists(entry.SpoolName))
            .OrderBy(entry => entry.QueuedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Paid orders from the app that this kiosk has no file for yet: fetched in
    /// queue order, one at a time, before the next job is chosen. One that fails
    /// waits (<see cref="FetchRetry"/>) and keeps its place; the jobs behind it
    /// whose files are here print meanwhile.
    /// </summary>
    private async Task FetchRemoteAsync(IReadOnlyList<QueueItem> queue, CancellationToken cancellationToken)
    {
        if (_remote is null)
        {
            return;
        }

        foreach (var item in queue.Where(item => item.Status == "Queued" && _journal.Find(item.JobId) is null))
        {
            if (_fetchFailedAt.TryGetValue(item.JobId, out var failedAt) && DateTime.UtcNow - failedAt < FetchRetry)
            {
                continue;
            }

            try
            {
                if (await _remote.FetchAsync(item.JobId, cancellationToken).ConfigureAwait(false) is not { } order)
                {
                    // Not one to fetch (a flash order whose file this kiosk lost):
                    // asked about again only after the pause.
                    _fetchFailedAt[item.JobId] = DateTime.UtcNow;
                    continue;
                }

                _journal.Save(new JournalEntry(
                    order.JobId, order.JobCode, JournalState.Queued, order.Options, order.RequiredSheets, 0, 0, null, false,
                    item.QueuedAt ?? DateTime.UtcNow, DateTime.UtcNow, null, order.Sides));
                _fetchFailedAt.Remove(item.JobId);

                _logger.LogInformation("Order {JobCode} from the app fetched into the spool.", order.JobCode);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _fetchFailedAt[item.JobId] = DateTime.UtcNow;
                _logger.LogWarning(exception, "The file of order {JobCode} could not be fetched; trying again later.", item.JobCode);
            }
        }
    }

    /// <returns>Whether the job went to the printer; false when it waits for one.</returns>
    private async Task<bool> PrintAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var printer = _selectedPrinter();

        if (string.IsNullOrWhiteSpace(printer))
        {
            _logger.LogWarning("No printer is selected; job {JobCode} waits.", entry.JobCode);
            return false;
        }

        var state = await _printers.GetStateAsync(printer, cancellationToken).ConfigureAwait(false);

        if (state is not (PrinterState.Online or PrinterState.TonerLow))
        {
            // Not started: the job keeps its place and waits for the printer.
            SetActivity(new PrinterActivity(null, null, state, PrinterErrors.For(state)));
            _logger.LogWarning("Printer {Printer} is {State}; job {JobCode} waits.", printer, state, entry.JobCode);
            return false;
        }

        var counterBefore = await ReadCounterAsync(printer, cancellationToken).ConfigureAwait(false);

        // Written before anything reaches the printer (see the class remarks).
        var printing = entry with
        {
            State = JournalState.Printing,
            Attempts = entry.Attempts + 1,
            Printer = printer,
            SpoolerJobId = null,
            StartedAt = DateTime.UtcNow,
            CounterBefore = counterBefore,
            CounterAfter = null,
            Recovered = false,
            UpdatedAt = DateTime.UtcNow,
        };
        _journal.Save(printing);
        SetActivity(new PrinterActivity(entry.JobId, entry.JobCode, state, null));

        await TryReportAsync(printing, "Printing", 0, 0, null, cancellationToken).ConfigureAwait(false);

        string? working = null;
        PrintOutcome outcome;

        try
        {
            working = await _spool.OpenAsync(entry.SpoolName, cancellationToken).ConfigureAwait(false);

            outcome = await _printers.PrintAsync(
                    new PrintRequest(
                        printer,
                        working,
                        entry.Options.Copies,
                        entry.Options.Duplex,
                        entry.Options.Color,
                        entry.Options.Paper,
                        $"NeoPrint-{entry.JobCode}",
                        entry.ContentType ?? PrintContentTypes.Pdf)
                    {
                        // Kept at once: a restart from here on can find the job.
                        Submitted = reference => _journal.Save(printing = printing with { SpoolerJobId = reference }),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Job {JobCode} could not be sent to the printer.", entry.JobCode);
            outcome = PrintOutcome.Failed(File.Exists(working) || working is null ? "printer.spoolFailed" : "printer.fileMissing");
        }
        finally
        {
            _spool.Discard(working);
        }

        var counterAfter = await ReadCounterAsync(printer, cancellationToken).ConfigureAwait(false);

        var finished = outcome.Succeeded
            ? printing with
            {
                State = JournalState.Printed,
                PrintedSheets = entry.RequiredSheets,
                CounterAfter = counterAfter,
                StatusReported = false,
                UpdatedAt = DateTime.UtcNow,
            }
            : printing with
            {
                State = JournalState.Interrupted,
                ErrorCode = outcome.ErrorCode ?? "printer.error",
                PrintedSheets = Math.Min(SheetsByCounter(printing, counterAfter), entry.RequiredSheets),
                CounterAfter = counterAfter,
                StatusReported = false,
                UpdatedAt = DateTime.UtcNow,
            };

        _journal.Save(finished);

        if (outcome.Succeeded)
        {
            _spool.Delete(entry.SpoolName);
        }

        SetActivity(new PrinterActivity(null, null, state, outcome.Succeeded ? null : finished.ErrorCode));
        _logger.LogInformation("Job {JobCode}: {State}.", entry.JobCode, finished.State);

        JobFinished?.Invoke(finished);

        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sends every final state the backend has not heard; each is safe to repeat.</summary>
    private async Task FlushReportsAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _journal.All().Where(entry => !entry.StatusReported
                                                             && entry.State is JournalState.Printed or JournalState.Interrupted))
        {
            var sides = entry.State == JournalState.Printed ? SidesOf(entry) : 0;
            var status = entry.State == JournalState.Printed ? "Completed" : "Interrupted";

            if (!await TryReportAsync(entry, status, sides, entry.PrintedSheets, entry.ErrorCode, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (entry.State == JournalState.Printed)
            {
                // Done and heard: nothing left to remember.
                _journal.Remove(entry.JobId);
            }
            else
            {
                _journal.Save(entry with { StatusReported = true, UpdatedAt = DateTime.UtcNow });
            }
        }
    }

    private async Task<bool> TryReportAsync(
        JournalEntry entry, string status, int sides, int sheets, string? errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await _backend.ReportAsync(entry.JobId, status, sides, sheets, errorCode, AttemptOf(entry), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DeviceRevokedException)
        {
            throw;
        }
        catch (KioskApiException exception) when (exception.Code == "common.illegalTransition")
        {
            // The backend has moved on (the job was cancelled, or an admin
            // settled it): this report no longer applies, and repeating it
            // would fail the same way for ever.
            _logger.LogWarning("Job {JobCode}: the backend no longer takes a {Status} report.", entry.JobCode, status);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Job {JobCode}: the {Status} report did not reach the backend; it will be sent again.", entry.JobCode, status);
            return false;
        }
    }

    /// <summary>What was printed on: the job's sides when all of it printed, else both sides of each sheet when duplex.</summary>
    private static int SidesOf(JournalEntry entry)
        => entry.Sides > 0 && entry.PrintedSheets >= entry.RequiredSheets
            ? entry.Sides
            : entry.Options.Duplex ? entry.PrintedSheets * 2 : entry.PrintedSheets;

    /// <summary>The attempt, for <c>print_attempts</c>; null before the job ever reached the printer.</summary>
    private static PrintAttemptReport? AttemptOf(JournalEntry entry)
        => entry.Attempts < 1
            ? null
            : new PrintAttemptReport(
                entry.Attempts,
                entry.Printer,
                entry.SpoolerJobId,
                entry.StartedAt,
                entry.State is JournalState.Printed or JournalState.Interrupted ? entry.UpdatedAt : null,
                entry.CounterBefore?.Value,
                entry.CounterAfter?.Value,
                entry.CounterBefore is { } counter ? (counter.CountsSheets ? "sheets" : "impressions") : null,
                entry.Recovered);

    private void SetActivity(PrinterActivity activity)
    {
        Activity = activity;
        ActivityChanged?.Invoke(activity);
    }
}
