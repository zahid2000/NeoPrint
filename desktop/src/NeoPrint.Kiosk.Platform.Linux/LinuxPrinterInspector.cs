using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Linux;

/// <summary>
/// A printer's details on Linux (plan section 7.10): IPP
/// <c>get-printer-attributes</c> through <c>ipptool</c> and the queue through
/// <c>lpstat</c>. A network printer's SNMP answers are added by the caller.
/// </summary>
public sealed class LinuxPrinterInspector : IPrinterInspector
{
    private readonly ICommandRunner _commands;

    public LinuxPrinterInspector(ICommandRunner commands) => _commands = commands;

    public async Task<PrinterDetails> InspectAsync(string printer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(printer);

        var details = new PrinterDetails(printer);

        try
        {
            var (exit, attributes) = await _commands
                .RunAsync(
                    "ipptool",
                    ["-T", "10", "-t", "-v", $"ipp://localhost/printers/{Uri.EscapeDataString(printer)}", "get-printer-attributes.test"],
                    cancellationToken)
                .ConfigureAwait(false);

            if (exit == 0)
            {
                details = IppPrinter.Describe(printer, IppAttributes.Parse(attributes));
            }

            var (_, queue) = await _commands
                .RunAsync("lpstat", ["-W", "not-completed", "-o", printer], cancellationToken)
                .ConfigureAwait(false);

            details = details with { SpoolerJobs = LpstatJobs.Parse(queue, details.State == "printing" ? "processing" : "pending") };
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // ipptool or lpstat is not installed: the printer is reported by
            // name alone, and the panel says what is missing.
        }

        return details;
    }
}
