namespace NeoPrint.Kiosk.Platform.Abstractions;

/// <summary>
/// Everything a printer tells about itself (plan section 7.10): who it is,
/// how it is, what it has left, what it has printed, what it can do and what
/// is waiting in the system's spooler.
/// </summary>
/// <remarks>
/// Windows reads it from WMI, Linux from IPP (<c>get-printer-attributes</c>)
/// and <c>lpstat</c>; network printers add SNMP (Printer MIB, RFC 3805) on
/// both. A value the printer does not give is null - the panel says "the
/// printer does not report this" - never a guess.
/// </remarks>
public interface IPrinterInspector
{
    Task<PrinterDetails> InspectAsync(string printer, CancellationToken cancellationToken = default);
}

/// <param name="Connection"><c>Usb</c>, <c>Network</c> or <c>Other</c>.</param>
/// <param name="Address">The host or IP of a network printer.</param>
/// <param name="State"><c>idle</c>, <c>printing</c> or <c>stopped</c>.</param>
/// <param name="StateReasons">The printer's own reasons, as it words them (<c>media-empty</c>, <c>toner-low</c>...); empty when there are none.</param>
/// <param name="LifetimePages">Everything the printer has ever printed.</param>
public sealed record PrinterDetails(
    string Name,
    string? Manufacturer = null,
    string? Model = null,
    string? SerialNumber = null,
    string? Firmware = null,
    string? DriverName = null,
    string? DriverVersion = null,
    string? Connection = null,
    string? Address = null,
    string? MacAddress = null,
    string? Uri = null,
    string? State = null,
    IReadOnlyList<string>? StateReasons = null,
    bool? Online = null,
    string? LastError = null,
    IReadOnlyList<PrinterSupply>? Supplies = null,
    IReadOnlyList<PrinterTray>? Trays = null,
    long? LifetimePages = null,
    long? ColorPages = null,
    long? MonoPages = null,
    PrinterFeatures? Features = null,
    IReadOnlyList<SpoolerJob>? SpoolerJobs = null)
{
    /// <summary>The second source's answer wherever the first had none: SNMP over IPP or WMI.</summary>
    public PrinterDetails FilledFrom(PrinterDetails? other)
    {
        if (other is null)
        {
            return this;
        }

        return this with
        {
            Manufacturer = Manufacturer ?? other.Manufacturer,
            Model = Model ?? other.Model,
            SerialNumber = SerialNumber ?? other.SerialNumber,
            Firmware = Firmware ?? other.Firmware,
            MacAddress = MacAddress ?? other.MacAddress,
            Supplies = Supplies is { Count: > 0 } ? Supplies : other.Supplies,
            Trays = Trays is { Count: > 0 } ? Trays : other.Trays,
            LifetimePages = LifetimePages ?? other.LifetimePages,
            ColorPages = ColorPages ?? other.ColorPages,
            MonoPages = MonoPages ?? other.MonoPages,
        };
    }
}

/// <param name="Type"><c>toner</c>, <c>ink</c>, <c>drum</c>, <c>fuser</c>, <c>waste</c> or <c>other</c>.</param>
/// <param name="Color">The colour's name as the printer gives it (<c>black</c>, <c>cyan</c>...), or its <c>#RRGGBB</c>.</param>
/// <param name="LevelPercent">0-100; null when the printer only says "some left" or nothing at all.</param>
public sealed record PrinterSupply(string Name, string? Type, string? Color, int? LevelPercent);

/// <param name="Media">The paper size loaded, as the printer names it.</param>
/// <param name="LevelPercent">How full; null when the tray has no sensor.</param>
public sealed record PrinterTray(string Name, string? Media, int? LevelPercent);

public sealed record PrinterFeatures(
    bool? Color = null,
    bool? Duplex = null,
    IReadOnlyList<string>? PaperSizes = null,
    IReadOnlyList<string>? Trays = null,
    int? ResolutionDpi = null,
    IReadOnlyList<string>? NativeFormats = null);

/// <summary>A job in the system's own spooler - CUPS or the Windows spooler - whoever sent it.</summary>
/// <param name="State">The spooler's word for it (<c>pending</c>, <c>processing</c>, <c>Printing</c>, <c>Error</c>...).</param>
public sealed record SpoolerJob(string Id, long? SizeBytes, string? State, DateTime? SubmittedAt);
