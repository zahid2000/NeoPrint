namespace NeoPrint.Domain.Kiosks;

/// <summary>Which printer of which kiosk a sample is about: the series' key.</summary>
public sealed class PrinterKey
{
    public Guid KioskId { get; set; }

    public string PrinterName { get; set; } = string.Empty;
}

/// <summary>
/// What a printer said about itself at one moment (plan section 7.10): a row
/// of the <c>printer_telemetry</c> time series, and - the newest one per
/// printer - what the printer detail shows.
/// </summary>
/// <remarks>
/// A value the printer does not give is null: the panel says "the printer
/// does not report this", never a guess.
/// </remarks>
public sealed class PrinterTelemetrySample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>When the server received it.</summary>
    public DateTime Timestamp { get; set; }

    public PrinterKey Printer { get; set; } = new();

    public Guid DeviceId { get; set; }

    /// <summary>Whether this is the printer the kiosk prints on.</summary>
    public bool Selected { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public string? SerialNumber { get; set; }

    public string? Firmware { get; set; }

    public string? DriverName { get; set; }

    public string? DriverVersion { get; set; }

    /// <summary><c>Usb</c>, <c>Network</c> or <c>Other</c>.</summary>
    public string? Connection { get; set; }

    public string? Address { get; set; }

    public string? MacAddress { get; set; }

    public string? Uri { get; set; }

    /// <summary><c>idle</c>, <c>printing</c> or <c>stopped</c>.</summary>
    public string? State { get; set; }

    /// <summary>The printer's own reasons, as it words them (<c>media-empty</c>, <c>toner-low</c>...).</summary>
    public List<string> StateReasons { get; set; } = [];

    public bool? Online { get; set; }

    public string? LastError { get; set; }

    public List<PrinterSupplyLevel> Supplies { get; set; } = [];

    public List<PrinterTrayLevel> Trays { get; set; } = [];

    /// <summary>Everything the printer has ever printed.</summary>
    public long? LifetimePages { get; set; }

    public long? ColorPages { get; set; }

    public long? MonoPages { get; set; }

    public PrinterFeatureSet? Features { get; set; }

    /// <summary>The jobs in the system's own spooler, whoever sent them.</summary>
    public List<PrinterSpoolerJob> SpoolerJobs { get; set; } = [];

    /// <summary>
    /// The spooler jobs seen and since when (job id → the first sample that
    /// had it), carried from sample to sample: how "stuck for five minutes"
    /// is known without reading the series back.
    /// </summary>
    public Dictionary<string, DateTime> JobsSeenSince { get; set; } = new(StringComparer.Ordinal);
}

/// <param name="Type"><c>toner</c>, <c>ink</c>, <c>drum</c>, <c>fuser</c>, <c>waste</c> or <c>other</c>.</param>
/// <param name="LevelPercent">0-100; null when the printer only says "some left" or nothing.</param>
public sealed record PrinterSupplyLevel(string Name, string? Type, string? Color, int? LevelPercent);

public sealed record PrinterTrayLevel(string Name, string? Media, int? LevelPercent);

public sealed class PrinterFeatureSet
{
    public bool? Color { get; set; }

    public bool? Duplex { get; set; }

    public List<string> PaperSizes { get; set; } = [];

    public List<string> Trays { get; set; } = [];

    public int? ResolutionDpi { get; set; }

    public List<string> NativeFormats { get; set; } = [];
}

public sealed record PrinterSpoolerJob(string Id, long? SizeBytes, string? State, DateTime? SubmittedAt);
