using System.Security.Cryptography;
using System.Text;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// What the kiosk reports about itself every Kiosk.TelemetrySeconds
// (plan section 7.9, POST kiosks/me/telemetry). A value the kiosk cannot read
// is left null - never guessed - and the panel says "no data" for it.
// ---------------------------------------------------------------------------

public sealed record TelemetryReport(
    DateTime SentAt,
    TelemetryAppPart App,
    TelemetrySystemPart System,
    TelemetryNetworkPart Network,
    TelemetryPeripheralsPart Peripherals,
    TelemetryWorkPart Work,
    TelemetryServicesPart Services);

public sealed record TelemetryAppPart(string? Version, DateTime? StartedAt, string? Screen, string? Session, DateTime? LastCrashAt);

public sealed record TelemetrySystemPart(
    string? Os,
    string? OsVersion,
    string? Hostname,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? SystemDiskFreeBytes,
    long? SpoolDiskFreeBytes,
    long? UptimeSeconds);

public sealed record TelemetryNetworkPart(
    string? ConnectionType,
    int? WifiSignalPercent,
    string? LocalIp,
    int? BackendLatencyMs,
    bool? HubConnected,
    DateTime? HubLastReconnectAt,
    int? HubDisconnectedSeconds);

public sealed record TelemetryPeripheralsPart(bool? FlashInserted, DateTime? QrScannerLastReadAt, int? ScreenWidth, int? ScreenHeight);

public sealed record TelemetryWorkPart(
    int? QueueLength,
    string? CurrentJob,
    int? JobsToday,
    int? SheetsToday,
    int? SpoolFiles,
    int? FailedFlashUploads,
    int? PendingCommands);

public sealed record TelemetryServicesPart(bool? LibreOffice, bool? Ghostscript, DateTime? ConfigurationAt, string? FeatureFlags);

/// <summary>
/// What only the running screen knows, read on the UI thread when a report is
/// made: where the customer is, and what is on the screen.
/// </summary>
/// <param name="Session"><c>Mobile</c>, <c>UserQr</c>, <c>Anonymous</c>, or null when nobody is at the kiosk.</param>
public sealed record TelemetryScreenState(
    string? Screen = null,
    string? Session = null,
    int? QueueLength = null,
    int? ScreenWidth = null,
    int? ScreenHeight = null);

/// <summary>
/// The counts telemetry reports that nothing else in the kiosk keeps: jobs and
/// sheets printed today, the job on the printer, the last QR read.
/// </summary>
/// <remarks>
/// Counted since the application started - they are not written to disk, so a
/// restart in the middle of the day starts them again. The server's own
/// figures (orders, the paper counter) are the ones money and stock go by;
/// these only show an operator what the kiosk has been doing.
/// </remarks>
public sealed class TelemetryCounters
{
    private readonly IClock _clock;
    private readonly object _gate = new();
    private DateOnly _day;
    private int _jobs;
    private int _sheets;
    private long _qrReadTicks;
    private string? _currentJob;

    public TelemetryCounters(IClock clock)
    {
        _clock = clock;
        _day = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());
    }

    /// <summary>A job reached its end; it counts for today when it printed anything.</summary>
    public void JobFinished(int printedSheets)
    {
        lock (_gate)
        {
            RollOver();

            if (printedSheets > 0)
            {
                _jobs++;
                _sheets += printedSheets;
            }
        }
    }

    /// <summary>The QR scanner read something at this moment.</summary>
    public void QrRead(DateTime at) => Interlocked.Exchange(ref _qrReadTicks, at.ToUniversalTime().Ticks);

    /// <summary>The job the printer is on, or null between jobs.</summary>
    public void Printing(string? jobCode) => Volatile.Write(ref _currentJob, jobCode);

    public (int Jobs, int Sheets) Today
    {
        get
        {
            lock (_gate)
            {
                RollOver();
                return (_jobs, _sheets);
            }
        }
    }

    public DateTime? QrLastReadAt
        => Interlocked.Read(ref _qrReadTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public string? CurrentJob => Volatile.Read(ref _currentJob);

    /// <summary>"Today" is the kiosk's own day, the one its customers live in.</summary>
    private void RollOver()
    {
        var today = DateOnly.FromDateTime(_clock.UtcNow.ToLocalTime());

        if (today != _day)
        {
            _day = today;
            _jobs = 0;
            _sheets = 0;
        }
    }
}

/// <summary>The kiosk's live connection, as telemetry reports it.</summary>
public sealed record HubState(bool Connected, DateTime? LastReconnectAt, DateTime? DisconnectedSince);

/// <summary>What the collector reads besides the machine itself; each is a question asked when a report is made.</summary>
public sealed class TelemetrySources
{
    public Func<TelemetryScreenState> Screen { get; init; } = () => new TelemetryScreenState();

    public Func<HubState?> Hub { get; init; } = () => null;

    public Func<CancellationToken, Task<bool?>> FlashInserted { get; init; } = _ => Task.FromResult<bool?>(null);

    public Func<int?> SpoolFiles { get; init; } = () => null;

    /// <summary>Flash uploads that did not reach the backend and wait to be sent again.</summary>
    public Func<int?> FailedFlashUploads { get; init; } = () => null;

    public Func<int?> PendingCommands { get; init; } = () => null;

    public Func<DateTime?> LastCrashAt { get; init; } = () => null;

    public Func<bool?> LibreOffice { get; init; } = () => null;

    public Func<bool?> Ghostscript { get; init; } = () => null;
}

/// <summary>Puts one telemetry report together (plan section 7.9).</summary>
public sealed class KioskTelemetryCollector
{
    private readonly ISystemMetrics _metrics;
    private readonly KioskRuntime _runtime;
    private readonly TelemetryCounters _counters;
    private readonly IClock _clock;
    private readonly DateTime _startedAt;

    public KioskTelemetryCollector(ISystemMetrics metrics, KioskRuntime runtime, TelemetryCounters counters, IClock clock)
    {
        _metrics = metrics;
        _runtime = runtime;
        _counters = counters;
        _clock = clock;
        _startedAt = clock.UtcNow;
    }

    public async Task<TelemetryReport> CollectAsync(TelemetrySources sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var now = _clock.UtcNow;
        var system = await _metrics.ReadAsync(cancellationToken).ConfigureAwait(false);
        var screen = sources.Screen();
        var hub = sources.Hub();
        var (jobs, sheets) = _counters.Today;

        return new TelemetryReport(
            now,
            new TelemetryAppPart(KioskRuntime.AppVersion, _startedAt, screen.Screen, screen.Session, sources.LastCrashAt()),
            new TelemetrySystemPart(
                system.Os,
                system.OsVersion,
                system.Hostname,
                system.CpuPercent,
                system.MemoryUsedBytes,
                system.MemoryTotalBytes,
                system.SystemDiskFreeBytes,
                system.SpoolDiskFreeBytes,
                system.UptimeSeconds),
            new TelemetryNetworkPart(
                system.ConnectionType,
                system.WifiSignalPercent,
                system.LocalIp,
                _runtime.LastBackendLatencyMs,
                hub?.Connected,
                hub?.LastReconnectAt,
                hub is { Connected: false, DisconnectedSince: { } since } ? (int)Math.Max(0, (now - since).TotalSeconds) : null),
            new TelemetryPeripheralsPart(
                await sources.FlashInserted(cancellationToken).ConfigureAwait(false),
                _counters.QrLastReadAt,
                screen.ScreenWidth,
                screen.ScreenHeight),
            new TelemetryWorkPart(
                screen.QueueLength,
                _counters.CurrentJob,
                jobs,
                sheets,
                sources.SpoolFiles(),
                sources.FailedFlashUploads(),
                sources.PendingCommands()),
            new TelemetryServicesPart(
                sources.LibreOffice(),
                sources.Ghostscript(),
                _runtime.ConfigurationFetchedAt,
                FeatureFingerprint(_runtime.Configuration?.Features)));
    }

    /// <summary>
    /// A short fingerprint of the feature flags the kiosk holds: two kiosks
    /// with the same flags show the same eight characters, and a kiosk still
    /// on yesterday's flags stands out.
    /// </summary>
    public static string? FeatureFingerprint(IReadOnlyDictionary<string, bool>? features)
    {
        if (features is null || features.Count == 0)
        {
            return null;
        }

        var text = string.Join(';', features.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={(pair.Value ? 1 : 0)}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();
    }
}
