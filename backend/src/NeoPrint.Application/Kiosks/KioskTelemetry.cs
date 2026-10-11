using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// Kiosk telemetry (plan section 7.9): POST kiosks/me/telemetry,
// GET admin/kiosks/{id}/telemetry, GET admin/kiosks/{id}/telemetry/history
// ---------------------------------------------------------------------------

/// <summary>What a kiosk reports about itself. Every group and every value is optional.</summary>
/// <param name="SentAt">The kiosk's own clock when it sent the report; the server measures the clock's offset from it.</param>
public sealed record KioskTelemetryReport(
    DateTime? SentAt,
    TelemetryApp? App,
    TelemetrySystem? System,
    TelemetryNetwork? Network,
    TelemetryPeripherals? Peripherals,
    TelemetryWork? Work,
    TelemetryServices? Services);

/// <summary>Stores a kiosk's report, checks it against the limits and tells the panel.</summary>
public sealed record ReportKioskTelemetryCommand(Guid DeviceId, KioskTelemetryReport Report) : IRequest;

/// <summary>What the server itself knows about the kiosk, shown next to what the kiosk reported.</summary>
/// <param name="Status">The kiosk status by name: the same text over REST and over the hub, which writes enums as numbers.</param>
public sealed record KioskTelemetryStatus(
    string Status,
    DateTime? LastHeartbeatAt,
    long OpenIncidents,
    IReadOnlyDictionary<string, PaperStockDto> Paper);

/// <summary>A kiosk's current state (plan section 7.9).</summary>
/// <param name="Sample">The latest report, or null when the kiosk has never sent one.</param>
/// <param name="Stale">The latest report is older than three reporting intervals: the numbers are not "now".</param>
public sealed record KioskTelemetryView(
    Guid KioskId, KioskTelemetrySample? Sample, bool Stale, int IntervalSeconds, KioskTelemetryStatus Status);

/// <summary>One point of a graph: the average over its slice of time.</summary>
public sealed record KioskTelemetryPoint(
    DateTime At,
    double? CpuPercent,
    double? MemoryUsedPercent,
    double? SystemDiskFreeBytes,
    double? SpoolDiskFreeBytes,
    double? BackendLatencyMs,
    double? QueueLength,
    int Samples);

public sealed record GetKioskTelemetryQuery(Guid AdminUserId, Guid KioskId) : IRequest<KioskTelemetryView>;

/// <summary>The graphs of the kiosk detail: at most <paramref name="Points"/> averaged points between the two times.</summary>
public sealed record GetKioskTelemetryHistoryQuery(Guid AdminUserId, Guid KioskId, DateTime? From, DateTime? To, int Points = 120)
    : IRequest<IReadOnlyList<KioskTelemetryPoint>>;

/// <summary>The <c>kiosk_telemetry</c> time series (plan section 5).</summary>
public interface IKioskTelemetryStore
{
    Task AddAsync(KioskTelemetrySample sample, CancellationToken cancellationToken = default);

    Task<KioskTelemetrySample?> LatestAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>The series cut into slices of <paramref name="sliceSeconds"/>, each averaged, oldest first.</summary>
    Task<IReadOnlyList<KioskTelemetryPoint>> HistoryAsync(
        Guid kioskId, DateTime from, DateTime to, int sliceSeconds, CancellationToken cancellationToken = default);
}

/// <summary>
/// The latest report of each kiosk, in Redis (<c>kiosk:status:{id}</c>, plan
/// section 7.9). Only a cache: when it is empty the latest row of the series
/// is the answer.
/// </summary>
public interface IKioskStatusCache
{
    Task SetAsync(KioskTelemetrySample sample, TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task<KioskTelemetrySample?> GetAsync(Guid kioskId, CancellationToken cancellationToken = default);
}

/// <summary>A limit a kiosk has been over for long enough to tell somebody.</summary>
public sealed record TelemetryAlert(string Condition, IncidentType Type, IncidentSeverity Severity, UserMessage Details);

/// <param name="Raise">Limits that have now been exceeded for long enough.</param>
/// <param name="Cleared">Limits the previous sample was over and this one is not.</param>
public sealed record TelemetryEvaluation(IReadOnlyList<TelemetryAlert> Raise, IReadOnlyList<string> Cleared);

/// <summary>
/// Checks a telemetry sample against the limits of plan section 7.9: disk
/// below 2 GB, memory above 90% for five minutes, the backend slower than
/// 2000 ms for five minutes, SignalR down for two minutes.
/// </summary>
/// <remarks>
/// Pure: the only state is in the samples themselves. Each sample records
/// since when it has been over each limit, taken from the sample before it,
/// so "for five minutes" needs neither a timer nor a look back through the
/// series - and survives a restart and a lost cache.
/// </remarks>
public static class TelemetryAlertEvaluator
{
    public const long DiskLowBytes = 2L * 1024 * 1024 * 1024;
    public const double MemoryHighPercent = 90;
    public const int BackendSlowMs = 2000;

    public static readonly TimeSpan MemoryHighFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackendSlowFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HubDownFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A gap this long between two samples breaks a streak: the kiosk was not
    /// reporting, so nothing is known about the time in between.
    /// </summary>
    public static readonly TimeSpan StreakBreak = TimeSpan.FromMinutes(3);

    /// <summary>The dedup key of the incident a condition opens: one open incident per kiosk and condition.</summary>
    public static string DedupKey(Guid kioskId, string condition) => $"telemetry:{kioskId:N}:{condition}";

    /// <summary>
    /// Fills in <see cref="KioskTelemetrySample.Breaches"/> of <paramref name="current"/>
    /// and says what to raise and what has cleared.
    /// </summary>
    public static TelemetryEvaluation Evaluate(KioskTelemetrySample? previous, KioskTelemetrySample current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var continues = previous is not null && current.Timestamp - previous.Timestamp <= StreakBreak;
        var before = continues ? previous!.Breaches : new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var raise = new List<TelemetryAlert>();

        current.Breaches = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        void Check(string condition, bool over, TimeSpan needed, Func<TimeSpan, TelemetryAlert> alert, DateTime? knownSince = null)
        {
            if (!over)
            {
                return;
            }

            var since = before.TryGetValue(condition, out var earlier) ? earlier : current.Timestamp;

            // The kiosk may know better than the server since when: it timed
            // its own disconnection.
            if (knownSince is { } known && known < since)
            {
                since = known;
            }

            current.Breaches[condition] = since;

            if (current.Timestamp - since >= needed)
            {
                raise.Add(alert(current.Timestamp - since));
            }
        }

        long? lowest = (current.System.SystemDiskFreeBytes, current.System.SpoolDiskFreeBytes) switch
        {
            ({ } system, { } spool) => Math.Min(system, spool),
            ({ } system, null) => system,
            (null, { } spool) => spool,
            _ => null,
        };

        Check(
            TelemetryCondition.DiskLow,
            lowest < DiskLowBytes,
            TimeSpan.Zero,
            _ => new TelemetryAlert(
                TelemetryCondition.DiskLow,
                IncidentType.DiskLow,
                IncidentSeverity.Medium,
                UserMessage.Of("incidents.telemetry.diskLow", ("freeMb", (lowest ?? 0) / (1024 * 1024)))));

        Check(
            TelemetryCondition.MemoryHigh,
            current.System.MemoryUsedPercent > MemoryHighPercent,
            MemoryHighFor,
            lasted => new TelemetryAlert(
                TelemetryCondition.MemoryHigh,
                IncidentType.MemoryHigh,
                IncidentSeverity.Medium,
                UserMessage.Of(
                    "incidents.telemetry.memoryHigh",
                    ("percent", (int)Math.Round(current.System.MemoryUsedPercent ?? 0)),
                    ("minutes", (int)lasted.TotalMinutes))));

        Check(
            TelemetryCondition.BackendSlow,
            current.Network.BackendLatencyMs > BackendSlowMs,
            BackendSlowFor,
            lasted => new TelemetryAlert(
                TelemetryCondition.BackendSlow,
                IncidentType.BackendSlow,
                IncidentSeverity.Medium,
                UserMessage.Of(
                    "incidents.telemetry.backendSlow",
                    ("ms", current.Network.BackendLatencyMs ?? 0),
                    ("minutes", (int)lasted.TotalMinutes))));

        Check(
            TelemetryCondition.HubDown,
            current.Network.HubConnected == false,
            HubDownFor,
            lasted => new TelemetryAlert(
                TelemetryCondition.HubDown,
                IncidentType.ConnectionLost,
                IncidentSeverity.High,
                UserMessage.Of("incidents.telemetry.hubDown", ("minutes", (int)lasted.TotalMinutes))),
            current.Network.HubDisconnectedSeconds is > 0 and var seconds
                ? current.Timestamp - TimeSpan.FromSeconds(seconds)
                : null);

        // Cleared is judged against the previous sample whatever the gap: an
        // incident opened before a silence still closes when the kiosk comes
        // back within its limits.
        IReadOnlyList<string> cleared = previous is null
            ? []
            : [.. previous.Breaches.Keys.Where(condition => !current.Breaches.ContainsKey(condition))];

        return new TelemetryEvaluation(raise, cleared);
    }
}

/// <summary>Kiosk telemetry: the kiosk's report, and the panel's reads.</summary>
public sealed class KioskTelemetryHandlers :
    IRequestHandler<ReportKioskTelemetryCommand>,
    IRequestHandler<GetKioskTelemetryQuery, KioskTelemetryView>,
    IRequestHandler<GetKioskTelemetryHistoryQuery, IReadOnlyList<KioskTelemetryPoint>>
{
    /// <summary>A kiosk that reports faster than this is not stored twice: nothing changed that a graph would show.</summary>
    public static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(5);

    /// <summary>The longest stretch one history request covers.</summary>
    public static readonly TimeSpan LongestHistory = TimeSpan.FromDays(31);

    private const int MaxText = 128;

    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskTelemetryStore _store;
    private readonly IKioskStatusCache _cache;
    private readonly IIncidentRepository _incidents;
    private readonly IncidentReporter _reporter;
    private readonly IAdminNotifier _admins;
    private readonly IPermissionResolver _permissions;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<KioskTelemetryHandlers> _logger;

    public KioskTelemetryHandlers(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IKioskTelemetryStore store,
        IKioskStatusCache cache,
        IIncidentRepository incidents,
        IncidentReporter reporter,
        IAdminNotifier admins,
        IPermissionResolver permissions,
        ISettingsReader settings,
        IClock clock,
        ILogger<KioskTelemetryHandlers> logger)
    {
        _devices = devices;
        _kiosks = kiosks;
        _store = store;
        _cache = cache;
        _incidents = incidents;
        _reporter = reporter;
        _admins = admins;
        _permissions = permissions;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="ReportKioskTelemetryCommand" />
    public async Task Handle(ReportKioskTelemetryCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("device");

        if (device.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        var now = _clock.UtcNow;
        var previous = await LatestAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (previous is not null && now - previous.Timestamp < MinimumSpacing)
        {
            return;
        }

        var sample = Clean(request.Report, kioskId, device.Id, now);
        var evaluation = TelemetryAlertEvaluator.Evaluate(previous, sample);

        await _store.AddAsync(sample, cancellationToken).ConfigureAwait(false);

        var interval = await IntervalAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Kept well past the interval: a kiosk that has gone quiet still
            // shows its last known state, marked stale.
            await _cache.SetAsync(sample, TimeSpan.FromSeconds(interval * 20), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The series has the sample; the cache is only the short way to it.
            _logger.LogWarning(exception, "The telemetry snapshot of kiosk {KioskId} could not be cached.", kioskId);
        }

        foreach (var alert in evaluation.Raise)
        {
            await _reporter
                .RaiseAsync(
                    alert.Type,
                    alert.Severity,
                    alert.Details,
                    TelemetryAlertEvaluator.DedupKey(kioskId, alert.Condition),
                    cancellationToken,
                    kioskId)
                .ConfigureAwait(false);
        }

        foreach (var condition in evaluation.Cleared)
        {
            var open = await _incidents
                .FindOpenByDedupKeyAsync(TelemetryAlertEvaluator.DedupKey(kioskId, condition), cancellationToken)
                .ConfigureAwait(false);

            if (open is not null)
            {
                open.Resolve(null, new UserMessage("incidents.resolution.telemetryRecovered").Encode(), now);
                await _incidents.UpdateAsync(open, cancellationToken).ConfigureAwait(false);
            }
        }

        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is not null)
        {
            await _admins
                .KioskTelemetryUpdatedAsync(
                    await ViewAsync(kiosk, sample, interval, now, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc cref="GetKioskTelemetryQuery" />
    public async Task<KioskTelemetryView> Handle(GetKioskTelemetryQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var sample = await LatestAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        var interval = await IntervalAsync(cancellationToken).ConfigureAwait(false);

        return await ViewAsync(kiosk, sample, interval, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetKioskTelemetryHistoryQuery" />
    public async Task<IReadOnlyList<KioskTelemetryPoint>> Handle(
        GetKioskTelemetryHistoryQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        var to = request.To ?? _clock.UtcNow;
        var from = request.From ?? to.AddHours(-1);

        if (from >= to)
        {
            throw new ValidationException([new ValidationError("from", "telemetry.rangeInvalid")]);
        }

        if (to - from > LongestHistory)
        {
            from = to - LongestHistory;
        }

        var points = Math.Clamp(request.Points, 10, 500);
        var interval = await IntervalAsync(cancellationToken).ConfigureAwait(false);

        // Never finer than the kiosk reports: a slice would hold one sample or none.
        var slice = Math.Max(interval, (int)Math.Ceiling((to - from).TotalSeconds / points));

        return await _store.HistoryAsync(kiosk.Id, from, to, slice, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cache first; the series when the cache has nothing - Redis is never the only copy.</summary>
    private async Task<KioskTelemetrySample?> LatestAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        try
        {
            if (await _cache.GetAsync(kioskId, cancellationToken).ConfigureAwait(false) is { } cached)
            {
                return cached;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The telemetry snapshot of kiosk {KioskId} could not be read from the cache.", kioskId);
        }

        return await _store.LatestAsync(kioskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> IntervalAsync(CancellationToken cancellationToken)
        => Math.Max(5, await _settings.GetIntAsync(Domain.Settings.SettingKeys.KioskTelemetrySeconds, 30, cancellationToken).ConfigureAwait(false));

    private async Task<KioskTelemetryView> ViewAsync(
        Kiosk kiosk, KioskTelemetrySample? sample, int interval, DateTime now, CancellationToken cancellationToken)
    {
        var open = await _incidents.CountOpenForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return new KioskTelemetryView(
            kiosk.Id,
            sample,
            sample is null || now - sample.Timestamp > TimeSpan.FromSeconds(interval * 3),
            interval,
            new KioskTelemetryStatus(
                kiosk.Status.ToString(),
                kiosk.LastHeartbeatAt,
                open,
                kiosk.PaperStock.ToDictionary(
                    pair => pair.Key,
                    pair => new PaperStockDto(pair.Value.Capacity, pair.Value.Current, pair.Value.LastRefillAt),
                    StringComparer.Ordinal)));
    }

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }

    // -----------------------------------------------------------------------
    // The kiosk is an untrusted device: what it sends is bounded before it is
    // stored or shown. Out-of-range numbers become "no data", text is cut.
    // -----------------------------------------------------------------------

    public static KioskTelemetrySample Clean(KioskTelemetryReport report, Guid kioskId, Guid deviceId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(report);

        var app = report.App ?? new TelemetryApp();
        var system = report.System ?? new TelemetrySystem();
        var network = report.Network ?? new TelemetryNetwork();
        var peripherals = report.Peripherals ?? new TelemetryPeripherals();
        var work = report.Work ?? new TelemetryWork();
        var services = report.Services ?? new TelemetryServices();

        return new KioskTelemetrySample
        {
            Timestamp = now,
            KioskId = kioskId,
            DeviceId = deviceId,
            App = new TelemetryApp
            {
                Version = Text(app.Version),
                StartedAt = Past(app.StartedAt, now),
                Screen = Text(app.Screen),
                Session = app.Session is "Mobile" or "UserQr" or "Anonymous" ? app.Session : null,
                LastCrashAt = Past(app.LastCrashAt, now),
            },
            System = new TelemetrySystem
            {
                Os = Text(system.Os),
                OsVersion = Text(system.OsVersion),
                Hostname = Text(system.Hostname),
                CpuPercent = system.CpuPercent is >= 0 and <= 100 ? Math.Round(system.CpuPercent.Value, 1) : null,
                MemoryTotalBytes = Positive(system.MemoryTotalBytes),
                MemoryUsedBytes = system.MemoryUsedBytes is >= 0 && system.MemoryUsedBytes <= (system.MemoryTotalBytes ?? long.MaxValue)
                    ? system.MemoryUsedBytes
                    : null,
                SystemDiskFreeBytes = NotNegative(system.SystemDiskFreeBytes),
                SpoolDiskFreeBytes = NotNegative(system.SpoolDiskFreeBytes),
                UptimeSeconds = NotNegative(system.UptimeSeconds),
                ClockOffsetMs = report.SentAt is { } sent ? (long)(sent.ToUniversalTime() - now).TotalMilliseconds : null,
            },
            Network = new TelemetryNetwork
            {
                ConnectionType = network.ConnectionType is "Ethernet" or "WiFi" or "Other" ? network.ConnectionType : null,
                WifiSignalPercent = network.WifiSignalPercent is >= 0 and <= 100 ? network.WifiSignalPercent : null,
                LocalIp = System.Net.IPAddress.TryParse(network.LocalIp, out var ip) ? ip.ToString() : null,
                BackendLatencyMs = network.BackendLatencyMs is >= 0 and <= 600_000 ? network.BackendLatencyMs : null,
                HubConnected = network.HubConnected,
                HubLastReconnectAt = Past(network.HubLastReconnectAt, now),
                HubDisconnectedSeconds = network.HubConnected == false && network.HubDisconnectedSeconds is >= 0 and <= 31_536_000
                    ? network.HubDisconnectedSeconds
                    : null,
            },
            Peripherals = new TelemetryPeripherals
            {
                FlashInserted = peripherals.FlashInserted,
                QrScannerLastReadAt = Past(peripherals.QrScannerLastReadAt, now),
                ScreenWidth = peripherals.ScreenWidth is > 0 and <= 20_000 ? peripherals.ScreenWidth : null,
                ScreenHeight = peripherals.ScreenHeight is > 0 and <= 20_000 ? peripherals.ScreenHeight : null,
            },
            Work = new TelemetryWork
            {
                QueueLength = Count(work.QueueLength),
                CurrentJob = Text(work.CurrentJob, 32),
                JobsToday = Count(work.JobsToday),
                SheetsToday = Count(work.SheetsToday),
                SpoolFiles = Count(work.SpoolFiles),
                FailedFlashUploads = Count(work.FailedFlashUploads),
                PendingCommands = Count(work.PendingCommands),
            },
            Services = new TelemetryServices
            {
                LibreOffice = services.LibreOffice,
                Ghostscript = services.Ghostscript,
                ConfigurationAt = Past(services.ConfigurationAt, now),
                FeatureFlags = Text(services.FeatureFlags, 32),
            },
        };
    }

    private static string? Text(string? value, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // No control characters: this text ends up in a log line and a panel.
        var clean = new string([.. value.Trim().Where(character => !char.IsControl(character))]);

        return clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>A time the kiosk names, unless it is in the future by more than its clock could be off.</summary>
    private static DateTime? Past(DateTime? value, DateTime now)
        => value is { } time && time.ToUniversalTime() <= now.AddDays(1) && time.Year >= 2020 ? time.ToUniversalTime() : null;

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static long? NotNegative(long? value) => value is >= 0 ? value : null;

    private static int? Count(int? value) => value is >= 0 and <= 1_000_000 ? value : null;
}
