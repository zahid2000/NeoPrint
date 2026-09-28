using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// The machine's printers plus the fake one, when <c>Kiosk:UseFakePrinter</c>
/// is on (plan section 7.7: "siyahıya NeoPrint Fake Printer əlavə olunur").
/// </summary>
/// <remarks>
/// A job goes to whichever service owns the printer's name, so a developer
/// machine can print to the fake printer and to "Microsoft Print to PDF" side
/// by side.
/// </remarks>
public sealed class CompositePrinterService : IPrinterService, IPrinterFormatSupport
{
    private readonly IPrinterService _machine;
    private readonly FakePrinterService _fake;

    public CompositePrinterService(IPrinterService machine, FakePrinterService fake)
    {
        _machine = machine;
        _fake = fake;
    }

    public async Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DetectedPrinter> real;

        try
        {
            real = await _machine.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The machine's printing is broken; the fake one still answers.
            real = [];
        }

        return [.. real, .. await _fake.DiscoverAsync(cancellationToken).ConfigureAwait(false)];
    }

    public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
        => For(printer).GetStateAsync(printer, cancellationToken);

    public Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return For(request.Printer).PrintAsync(request, cancellationToken);
    }

    public Task<IReadOnlySet<string>> GetNativeFormatsAsync(string printer, CancellationToken cancellationToken = default)
        => (For(printer) as IPrinterFormatSupport ?? NoNativeFormats.Instance).GetNativeFormatsAsync(printer, cancellationToken);

    private IPrinterService For(string printer)
        => printer == FakePrinterService.FakePrinterName ? _fake : _machine;
}
