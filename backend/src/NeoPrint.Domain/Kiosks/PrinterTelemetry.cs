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

/// <summary>What can happen to a printer, as <see cref="PrinterEvent.Kind"/> names it.</summary>
public static class PrinterEventKind
{
    /// <summary><c>idle</c>, <c>printing</c> or <c>stopped</c> became another of them.</summary>
    public const string State = "State";

    /// <summary>The printer became reachable or unreachable.</summary>
    public const string Online = "Online";

    /// <summary>The printer's own list of reasons changed, whatever they are.</summary>
    public const string Reasons = "Reasons";

    /// <summary>The printer reported a new error text.</summary>
    public const string Error = "Error";

    /// <summary>A consumable's level went up by a cartridge's worth: it was replaced.</summary>
    public const string SupplyReplaced = "SupplyReplaced";

    /// <summary>A tray's level went up by a ream's worth: paper was put in.</summary>
    public const string PaperRefilled = "PaperRefilled";

    public const string JamStarted = "JamStarted";

    public const string JamEnded = "JamEnded";

    /// <summary>The printer says it has no paper to print on.</summary>
    public const string PaperOutStarted = "PaperOutStarted";

    public const string PaperOutEnded = "PaperOutEnded";

    public const string CoverOpened = "CoverOpened";

    public const string CoverClosed = "CoverClosed";
}

/// <summary>
/// Something that happened to a printer, found by comparing two readings of
/// it and written down at that moment: a row of <c>printer_events</c> (plan
/// section 7.10, "Tarixçə").
/// </summary>
/// <remarks>
/// Kept apart from the readings and for much longer. The readings are a
/// minute-by-minute series that expires in weeks; when a cartridge was last
/// replaced and how many pages the one before it lasted must still be known
/// a year later (plan sections 2.15, 9.9).
/// </remarks>
public sealed class PrinterEvent
{
    /// <summary>
    /// Derived from the reading before the change, the kind and the subject:
    /// the same change found twice is the same row.
    /// </summary>
    public Guid Id { get; set; }

    public Guid KioskId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    /// <summary>When the server received the reading that showed it.</summary>
    public DateTime At { get; set; }

    /// <summary>One of <see cref="PrinterEventKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The supply or the tray it is about, or the printer's reason for it; null when it is about the printer as a whole.</summary>
    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    /// <summary>The level before, in percent, when the event is about a level.</summary>
    public int? FromLevel { get; set; }

    public int? ToLevel { get; set; }

    /// <summary>The printer's page counter at that moment: what "pages per cartridge" is counted from.</summary>
    public long? LifetimePages { get; set; }
}
