namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// One report of a kiosk's current state (plan section 7.9): a row of the
/// <c>kiosk_telemetry</c> time series, and - the newest one - the snapshot the
/// panel shows.
/// </summary>
/// <remarks>
/// Every value the kiosk could not read is null, never a guess: the panel
/// says "no data" for it. What the server itself knows - the kiosk's status,
/// its paper counters, its open incidents - is not stored here but added when
/// the snapshot is read.
/// </remarks>
public sealed class KioskTelemetrySample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>When the server received it; the series is ordered by this.</summary>
    public DateTime Timestamp { get; set; }

    public Guid KioskId { get; set; }

    public Guid DeviceId { get; set; }

    public TelemetryApp App { get; set; } = new();

    public TelemetrySystem System { get; set; } = new();

    public TelemetryNetwork Network { get; set; } = new();

    public TelemetryPeripherals Peripherals { get; set; } = new();

    public TelemetryWork Work { get; set; } = new();

    public TelemetryServices Services { get; set; } = new();

    /// <summary>The camera (T-482); empty on a kiosk where it is not turned on.</summary>
    public TelemetryCamera Camera { get; set; } = new();

    /// <summary>
    /// The limits this sample is over and since when (<see cref="TelemetryCondition"/>
    /// → the first sample that was over it). Carried from sample to sample, so
    /// "for five minutes" is known without reading the series back.
    /// </summary>
    public Dictionary<string, DateTime> Breaches { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The kiosk application itself.</summary>
public sealed class TelemetryApp
{
    public string? Version { get; set; }

    public DateTime? StartedAt { get; set; }

    /// <summary>The screen the customer is on, by its name in the kiosk (<c>Home</c>, <c>Options</c>...).</summary>
    public string? Screen { get; set; }

    /// <summary><c>Mobile</c>, <c>UserQr</c>, <c>Anonymous</c>, or null when nobody is at the kiosk.</summary>
    public string? Session { get; set; }

    public DateTime? LastCrashAt { get; set; }
}

/// <summary>The machine.</summary>
public sealed class TelemetrySystem
{
    public string? Os { get; set; }

    public string? OsVersion { get; set; }

    public string? Hostname { get; set; }

    /// <summary>0-100, over the interval since the previous sample.</summary>
    public double? CpuPercent { get; set; }

    public long? MemoryUsedBytes { get; set; }

    public long? MemoryTotalBytes { get; set; }

    /// <summary>Free space on the volume the system is on.</summary>
    public long? SystemDiskFreeBytes { get; set; }

    /// <summary>Free space on the volume the print spool is on.</summary>
    public long? SpoolDiskFreeBytes { get; set; }

    public long? UptimeSeconds { get; set; }

    /// <summary>
    /// The kiosk's clock minus the server's, in milliseconds, measured when the
    /// sample arrived: how far the kiosk's clock is off, give or take the time
    /// the request took.
    /// </summary>
    public long? ClockOffsetMs { get; set; }

    public double? MemoryUsedPercent
        => MemoryUsedBytes is { } used && MemoryTotalBytes is > 0 and var total ? Math.Round(100.0 * used / total, 1) : null;
}

/// <summary>The kiosk's connections.</summary>
public sealed class TelemetryNetwork
{
    /// <summary><c>Ethernet</c>, <c>WiFi</c> or <c>Other</c>.</summary>
    public string? ConnectionType { get; set; }

    /// <summary>0-100, Wi-Fi only.</summary>
    public int? WifiSignalPercent { get; set; }

    public string? LocalIp { get; set; }

    /// <summary>How long the kiosk's last request to the backend took.</summary>
    public int? BackendLatencyMs { get; set; }

    /// <summary>Whether the kiosk's SignalR connection is up.</summary>
    public bool? HubConnected { get; set; }

    public DateTime? HubLastReconnectAt { get; set; }

    /// <summary>How long the SignalR connection has been down, when it is.</summary>
    public int? HubDisconnectedSeconds { get; set; }
}

/// <summary>What is plugged into the kiosk.</summary>
public sealed class TelemetryPeripherals
{
    public bool? FlashInserted { get; set; }

    /// <summary>
    /// The last time the QR scanner read anything. A scanner is a keyboard to
    /// the system, so "connected" cannot be known - only that it worked then.
    /// </summary>
    public DateTime? QrScannerLastReadAt { get; set; }

    public int? ScreenWidth { get; set; }

    public int? ScreenHeight { get; set; }
}

/// <summary>What the kiosk is doing.</summary>
public sealed class TelemetryWork
{
    public int? QueueLength { get; set; }

    /// <summary>The job being printed, by its code.</summary>
    public string? CurrentJob { get; set; }

    public int? JobsToday { get; set; }

    public int? SheetsToday { get; set; }

    /// <summary>Encrypted files waiting in the local spool.</summary>
    public int? SpoolFiles { get; set; }

    /// <summary>Flash uploads that failed since the application started.</summary>
    public int? FailedFlashUploads { get; set; }

    /// <summary>Commands received and not yet carried out.</summary>
    public int? PendingCommands { get; set; }
}

/// <summary>What the kiosk has installed and loaded.</summary>
public sealed class TelemetryServices
{
    public bool? LibreOffice { get; set; }

    public bool? Ghostscript { get; set; }

    /// <summary>When the configuration the kiosk works from was fetched.</summary>
    public DateTime? ConfigurationAt { get; set; }

    /// <summary>A short fingerprint of the feature flags the kiosk holds.</summary>
    public string? FeatureFlags { get; set; }
}

/// <summary>The kiosk camera (T-482): whether it records, and what it keeps on the kiosk's disk.</summary>
public sealed class TelemetryCamera
{
    /// <summary>The camera is turned on for this kiosk (the <c>kiosk_camera</c> flag).</summary>
    public bool? Enabled { get; set; }

    /// <summary>A camera is plugged in and found.</summary>
    public bool? Connected { get; set; }

    /// <summary>The recorder is running and writing.</summary>
    public bool? Recording { get; set; }

    /// <summary><c>Continuous</c> or <c>Activity</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Why it does not record, as a code from <see cref="CameraErrors"/>.</summary>
    public string? Error { get; set; }

    /// <summary>The sealed recordings on the kiosk's disk.</summary>
    public long? StoredBytes { get; set; }

    public int? Segments { get; set; }

    /// <summary>The oldest recording kept: how far back the kiosk can show.</summary>
    public DateTime? OldestAt { get; set; }

    /// <summary>When the last finished segment was sealed.</summary>
    public DateTime? LastSegmentAt { get; set; }
}

/// <summary>Why a kiosk's camera does not record (T-482).</summary>
public static class CameraErrors
{
    /// <summary>No camera is plugged in.</summary>
    public const string NoCamera = "noCamera";

    /// <summary>FFmpeg is not installed, or has no encoder the kiosk may use.</summary>
    public const string NoRecorder = "noRecorder";

    /// <summary>The configuration has no camera key: nothing could be sealed.</summary>
    public const string NoKey = "noKey";

    /// <summary>The recorder keeps stopping.</summary>
    public const string RecorderFailed = "recorderFailed";

    /// <summary>Too little free disk to record without starving the print spool.</summary>
    public const string DiskFull = "diskFull";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NoCamera, NoRecorder, NoKey, RecorderFailed, DiskFull,
    };
}

/// <summary>The limits a sample is checked against (plan section 7.9).</summary>
public static class TelemetryCondition
{
    /// <summary>Less than 2 GB free on the system or the spool volume.</summary>
    public const string DiskLow = "disk";

    /// <summary>Memory above 90% for five minutes.</summary>
    public const string MemoryHigh = "memory";

    /// <summary>The backend taking more than two seconds to answer, for five minutes.</summary>
    public const string BackendSlow = "latency";

    /// <summary>The SignalR connection down for two minutes.</summary>
    public const string HubDown = "hub";

    /// <summary>The kiosk's clock a minute or more off the server's, for five minutes.</summary>
    public const string ClockOff = "clock";

    /// <summary>The camera turned on, and not recording for ten minutes (T-482).</summary>
    public const string CameraDown = "camera";
}
