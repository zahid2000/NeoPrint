using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>What the queue worker needs from the backend; <see cref="KioskApiClient"/> in the application.</summary>
public interface IPrintBackend
{
    Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken);

    Task ReportAsync(Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, CancellationToken cancellationToken);
}

/// <summary>The backend calls of <see cref="PrintQueueWorker"/>, over the kiosk's API client.</summary>
public sealed class ApiPrintBackend : IPrintBackend
{
    private readonly KioskApiClient _api;

    public ApiPrintBackend(KioskApiClient api) => _api = api;

    public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken) => _api.GetQueueAsync(cancellationToken);

    public Task ReportAsync(Guid jobId, string status, int printedSides, int printedSheets, string? errorCode, CancellationToken cancellationToken)
        => _api.ReportPrintJobStatusAsync(jobId, status, printedSides, printedSheets, errorCode, cancellationToken);
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

    private readonly IPrintBackend _backend;
    private readonly IPrinterService _printers;
    private readonly SpoolStore _spool;
    private readonly JobJournal _journal;
    private readonly Func<string?> _selectedPrinter;
    private readonly ILogger<PrintQueueWorker> _logger;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _printerLock = new(1, 1);

    public PrintQueueWorker(
        IPrintBackend backend,
        IPrinterService printers,
        SpoolStore spool,
        JobJournal journal,
        Func<string?> selectedPrinter,
        ILogger<PrintQueueWorker> logger)
    {
        _backend = backend;
        _printers = printers;
        _spool = spool;
        _journal = journal;
        _selectedPrinter = selectedPrinter;
        _logger = logger;
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
    public void Accept(Guid jobId, string jobCode, JobOptions options, int requiredSheets, DateTime queuedAt)
    {
        _journal.Save(new JournalEntry(
            jobId, jobCode, JournalState.Queued, options, requiredSheets, 0, 0, null, false, queuedAt, DateTime.UtcNow));
        Wake();
    }

    /// <summary>
    /// After a restart: a job that was at the printer is reported
    /// interrupted, never printed again; reports that did not reach the
    /// backend are sent now (plan step 3 DoD: no double print).
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        _spool.ClearWork();

        foreach (var entry in _journal.All().Where(entry => entry.State == JournalState.Printing))
        {
            _logger.LogWarning(
                "Job {JobCode} was at the printer when the kiosk stopped; reporting it interrupted, not printing it again.",
                entry.JobCode);

            _journal.Save(entry with
            {
                State = JournalState.Interrupted,
                ErrorCode = "printer.restarted",
                StatusReported = false,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);
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

        var next = await NextAsync(cancellationToken).ConfigureAwait(false);

        if (next is null || Paused)
        {
            return;
        }

        await _printerLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await PrintAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _printerLock.Release();
        }

        // There may be more; do not wait for the tick.
        Wake();
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

            foreach (var stale in _journal.All().Where(entry => entry.State == JournalState.Queued && !live.Contains(entry.JobId)))
            {
                _logger.LogInformation("Job {JobCode} left the queue before it printed; removing its file.", stale.JobCode);
                _spool.Delete(stale.SpoolName);
                _journal.Remove(stale.JobId);
            }

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

    private async Task PrintAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        var printer = _selectedPrinter();

        if (string.IsNullOrWhiteSpace(printer))
        {
            _logger.LogWarning("No printer is selected; job {JobCode} waits.", entry.JobCode);
            return;
        }

        var state = await _printers.GetStateAsync(printer, cancellationToken).ConfigureAwait(false);

        if (state is not (PrinterState.Online or PrinterState.TonerLow))
        {
            // Not started: the job keeps its place and waits for the printer.
            SetActivity(new PrinterActivity(null, null, state, PrinterErrors.For(state)));
            _logger.LogWarning("Printer {Printer} is {State}; job {JobCode} waits.", printer, state, entry.JobCode);
            return;
        }

        // Written before anything reaches the printer (see the class remarks).
        var printing = entry with { State = JournalState.Printing, Attempts = entry.Attempts + 1, UpdatedAt = DateTime.UtcNow };
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
                        $"NeoPrint-{entry.JobCode}"),
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

        var finished = outcome.Succeeded
            ? printing with { State = JournalState.Printed, PrintedSheets = entry.RequiredSheets, StatusReported = false, UpdatedAt = DateTime.UtcNow }
            : printing with { State = JournalState.Interrupted, ErrorCode = outcome.ErrorCode ?? "printer.error", StatusReported = false, UpdatedAt = DateTime.UtcNow };

        _journal.Save(finished);

        if (outcome.Succeeded)
        {
            _spool.Delete(entry.SpoolName);
        }

        SetActivity(new PrinterActivity(null, null, state, outcome.Succeeded ? null : finished.ErrorCode));
        _logger.LogInformation("Job {JobCode}: {State}.", entry.JobCode, finished.State);

        JobFinished?.Invoke(finished);

        await FlushReportsAsync(cancellationToken).ConfigureAwait(false);
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
            await _backend.ReportAsync(entry.JobId, status, sides, sheets, errorCode, cancellationToken).ConfigureAwait(false);
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

    /// <summary>What was printed on: the job's sheets, both sides when duplex.</summary>
    private static int SidesOf(JournalEntry entry) => entry.Options.Duplex ? entry.PrintedSheets * 2 : entry.PrintedSheets;

    private void SetActivity(PrinterActivity activity)
    {
        Activity = activity;
        ActivityChanged?.Invoke(activity);
    }
}
