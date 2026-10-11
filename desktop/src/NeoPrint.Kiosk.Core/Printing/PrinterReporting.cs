using System.Text.Json;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// Tells the backend which printers this kiosk can see: at start, every
/// minute when the list changed, and when asked (plan section 7.7).
/// </summary>
public sealed class PrinterReporter
{
    private readonly IPrinterService _printers;
    private readonly Func<IReadOnlyCollection<DetectedPrinter>, CancellationToken, Task> _report;
    private readonly ILogger<PrinterReporter> _logger;
    private string? _lastReported;

    public PrinterReporter(
        IPrinterService printers,
        Func<IReadOnlyCollection<DetectedPrinter>, CancellationToken, Task> report,
        ILogger<PrinterReporter> logger)
    {
        _printers = printers;
        _report = report;
        _logger = logger;
    }

    /// <summary>The list as last discovered.</summary>
    public IReadOnlyList<DetectedPrinter> Current { get; private set; } = [];

    /// <summary>Discovers and reports; unless <paramref name="force"/>, only when the list changed.</summary>
    /// <returns>Whether a report was sent.</returns>
    public async Task<bool> ReportAsync(bool force, CancellationToken cancellationToken)
    {
        var found = await _printers.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        Current = found;

        var fingerprint = JsonSerializer.Serialize(found.OrderBy(printer => printer.Name, StringComparer.Ordinal));

        if (!force && fingerprint == _lastReported)
        {
            return false;
        }

        await _report(found, cancellationToken).ConfigureAwait(false);
        _lastReported = fingerprint;

        _logger.LogInformation("Reported {Count} printers to the backend.", found.Count);

        return true;
    }
}

/// <summary>The kiosk's built-in test page (plan section 7.7).</summary>
public interface ITestPageSource
{
    /// <summary>Writes a one-page PDF naming the kiosk, the printer and the time, with colour and duplex checks.</summary>
    Task<string> CreateAsync(string folder, TestPageInfo info, CancellationToken cancellationToken);
}

/// <param name="Lines">What the page says, already in the kiosk's language: title first.</param>
public sealed record TestPageInfo(IReadOnlyList<string> Lines);

/// <summary>
/// Carries out the commands the panel sends (plan section 7.8): the printer
/// ones in step 3.
/// </summary>
/// <remarks>
/// A command arrives over the hub, or from <c>GET kiosks/me/commands</c>
/// after a reconnect, so the same one can come twice; the ids already done
/// are remembered and the backend ignores a repeated answer anyway.
/// </remarks>
public sealed class CommandProcessor
{
    private readonly KioskApiClient _api;
    private readonly PrinterReporter _reporter;
    private readonly PrintQueueWorker _worker;
    private readonly IPrinterService _printers;
    private readonly ITestPageSource _testPage;
    private readonly SpoolStore _spool;
    private readonly Func<KioskConfiguration?> _configuration;
    private readonly Func<KioskConfiguration, string, DateTime, IReadOnlyList<string>> _testPageText;
    private readonly ILogger<CommandProcessor> _logger;
    private readonly HashSet<Guid> _done = [];

    /// <summary>Commands received and still being carried out; telemetry reports the count (plan section 7.9).</summary>
    public int InProgress => _done.Count;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandProcessor(
        KioskApiClient api,
        PrinterReporter reporter,
        PrintQueueWorker worker,
        IPrinterService printers,
        ITestPageSource testPage,
        SpoolStore spool,
        Func<KioskConfiguration?> configuration,
        Func<KioskConfiguration, string, DateTime, IReadOnlyList<string>> testPageText,
        ILogger<CommandProcessor> logger)
    {
        _testPageText = testPageText;
        _api = api;
        _reporter = reporter;
        _worker = worker;
        _printers = printers;
        _testPage = testPage;
        _spool = spool;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Picks up what arrived while the kiosk was not listening.</summary>
    public async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        foreach (var command in await _api.GetOpenCommandsAsync(cancellationToken).ConfigureAwait(false))
        {
            await HandleAsync(command.Id, command.Type, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleAsync(Guid commandId, string type, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_done.Add(commandId))
            {
                return;
            }

            _logger.LogInformation("Command {CommandId}: {Type}.", commandId, type);

            var (succeeded, messageKey, sheets) = type switch
            {
                "RefreshPrinters" => await RefreshAsync(cancellationToken).ConfigureAwait(false),
                "TestPrint" => await TestPrintAsync(cancellationToken).ConfigureAwait(false),
                _ => (false, "kioskCommands.unsupported", 0),
            };

            await _api.CompleteCommandAsync(commandId, succeeded, messageKey, sheets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeviceRevokedException)
        {
            // Not answered: it stays open on the backend and is picked up again.
            _done.Remove(commandId);
            _logger.LogError(exception, "Command {CommandId} ({Type}) failed; it will be tried again.", commandId, type);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool, string, int)> RefreshAsync(CancellationToken cancellationToken)
    {
        await _reporter.ReportAsync(force: true, cancellationToken).ConfigureAwait(false);
        return (true, "kioskCommands.printersReported", 0);
    }

    /// <summary>
    /// Waits for the job at the printer, then prints the test page before the
    /// next one; it takes no place in the queue and costs nothing (plan 7.7).
    /// </summary>
    private Task<(bool, string, int)> TestPrintAsync(CancellationToken cancellationToken)
        => _worker.WithPrinterAsync(
            async () =>
            {
                var configuration = _configuration();
                var printer = configuration?.SelectedPrinter;

                if (configuration is null || string.IsNullOrWhiteSpace(printer))
                {
                    return (false, "printer.notSelected", 0);
                }

                var folder = _spool.NewWorkFolder();

                try
                {
                    var pdf = await _testPage.CreateAsync(
                            folder,
                            new TestPageInfo(_testPageText(configuration, printer, DateTime.UtcNow)),
                            cancellationToken)
                        .ConfigureAwait(false);

                    var outcome = await _printers.PrintAsync(
                            new PrintRequest(printer, pdf, 1, false, configuration.EffectiveCapabilities.Color, "A4", "NeoPrint-test"),
                            cancellationToken)
                        .ConfigureAwait(false);

                    return outcome.Succeeded
                        ? (true, "kioskCommands.testPagePrinted", 1)
                        : (false, outcome.ErrorCode ?? "printer.error", 0);
                }
                finally
                {
                    _spool.Discard(folder);
                }
            },
            cancellationToken);
}
