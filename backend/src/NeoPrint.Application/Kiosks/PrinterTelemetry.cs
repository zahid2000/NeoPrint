using MediatR;

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
// Printer telemetry (plan section 7.10): POST kiosks/me/printers/telemetry,
// GET admin/kiosks/{id}/printers/telemetry (+ /history, /events)
// ---------------------------------------------------------------------------

/// <summary>What a kiosk reports about one printer. Everything but the name is optional.</summary>
public sealed record PrinterTelemetryReport(
    string? Name,
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
    IReadOnlyList<PrinterSupplyLevel>? Supplies = null,
    IReadOnlyList<PrinterTrayLevel>? Trays = null,
    long? LifetimePages = null,
    long? ColorPages = null,
    long? MonoPages = null,
    PrinterFeatureSet? Features = null,
    IReadOnlyList<PrinterSpoolerJob>? SpoolerJobs = null);

/// <summary>Stores what the kiosk's printers said, checks the spooler for a stuck job and tells the panel.</summary>
public sealed record ReportPrinterTelemetryCommand(Guid DeviceId, IReadOnlyList<PrinterTelemetryReport> Printers) : IRequest;

/// <param name="Stale">The printer has not been heard about for a while: the numbers are not "now".</param>
public sealed record PrinterTelemetryView(Guid KioskId, PrinterTelemetrySample Sample, bool Stale);

/// <summary>One point of a printer's graphs: the last reading in its slice of time.</summary>
public sealed record PrinterTelemetryPoint(DateTime At, long? LifetimePages, IReadOnlyDictionary<string, int?> SupplyLevels);

/// <summary>Something that changed between two readings of a printer.</summary>
/// <param name="Kind"><c>State</c>, <c>Online</c>, <c>Reasons</c>, <c>Error</c> or <c>SupplyReplaced</c>.</param>
public sealed record PrinterTelemetryEvent(DateTime At, string Kind, string? From, string? To);

/// <summary>The latest reading of every printer of a kiosk, the selected one first.</summary>
public sealed record GetPrinterTelemetryQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<PrinterTelemetryView>>;

public sealed record GetPrinterTelemetryHistoryQuery(
    Guid AdminUserId, Guid KioskId, string Printer, DateTime? From, DateTime? To, int Points = 120)
    : IRequest<IReadOnlyList<PrinterTelemetryPoint>>;

/// <summary>What changed about a printer between two times, newest first (plan section 7.10, "Tarixçə").</summary>
public sealed record GetPrinterTelemetryEventsQuery(Guid AdminUserId, Guid KioskId, string Printer, DateTime? From, DateTime? To)
    : IRequest<IReadOnlyList<PrinterTelemetryEvent>>;

/// <summary>The <c>printer_telemetry</c> time series (plan section 5).</summary>
public interface IPrinterTelemetryStore
{
    Task AddManyAsync(IReadOnlyCollection<PrinterTelemetrySample> samples, CancellationToken cancellationToken = default);

    /// <summary>The newest sample of each printer of the kiosk since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<PrinterTelemetrySample>> LatestAsync(Guid kioskId, DateTime since, CancellationToken cancellationToken = default);

    Task<PrinterTelemetrySample?> LatestAsync(Guid kioskId, string printer, CancellationToken cancellationToken = default);

    /// <summary>The last sample of each slice of <paramref name="sliceSeconds"/>, oldest first.</summary>
    Task<IReadOnlyList<PrinterTelemetrySample>> SlicesAsync(
        Guid kioskId, string printer, DateTime from, DateTime to, int sliceSeconds, CancellationToken cancellationToken = default);

    /// <summary>Every sample between the two times, oldest first, at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<PrinterTelemetrySample>> RangeAsync(
        Guid kioskId, string printer, DateTime from, DateTime to, int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a sequence of readings of one printer says happened to it (plan
/// section 7.10): pure, so the history is the series itself and needs no log
/// of its own.
/// </summary>
public static class PrinterTelemetryChanges
{
    /// <summary>A level that goes up by this much was a new cartridge, not a sensor's wobble.</summary>
    public const int ReplacedByPoints = 20;

    /// <summary>A job in the system spooler for this long is stuck (plan section 7.10).</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    public static string SpoolerDedupKey(Guid kioskId, string printer) => $"spooler:{kioskId:N}:{printer}";

    public static IReadOnlyList<PrinterTelemetryEvent> Between(IReadOnlyList<PrinterTelemetrySample> oldestFirst)
    {
        ArgumentNullException.ThrowIfNull(oldestFirst);

        // One group per reading: the newest reading first, and within a
        // reading always in the same order - state, reachability, reasons,
        // error, supplies.
        var readings = new List<List<PrinterTelemetryEvent>>();

        for (var i = 1; i < oldestFirst.Count; i++)
        {
            var before = oldestFirst[i - 1];
            var after = oldestFirst[i];
            var at = after.Timestamp;
            var events = new List<PrinterTelemetryEvent>();
            readings.Add(events);

            if (before.State != after.State && after.State is not null)
            {
                events.Add(new PrinterTelemetryEvent(at, "State", before.State, after.State));
            }

            if (before.Online != after.Online && after.Online is not null)
            {
                events.Add(new PrinterTelemetryEvent(at, "Online", Text(before.Online), Text(after.Online)));
            }

            var reasonsBefore = string.Join(", ", before.StateReasons.Order(StringComparer.Ordinal));
            var reasonsAfter = string.Join(", ", after.StateReasons.Order(StringComparer.Ordinal));

            if (reasonsBefore != reasonsAfter)
            {
                events.Add(new PrinterTelemetryEvent(at, "Reasons", Empty(reasonsBefore), Empty(reasonsAfter)));
            }

            if (after.LastError is not null && after.LastError != before.LastError)
            {
                events.Add(new PrinterTelemetryEvent(at, "Error", before.LastError, after.LastError));
            }

            foreach (var supply in after.Supplies)
            {
                var was = before.Supplies.FirstOrDefault(candidate => candidate.Name == supply.Name)?.LevelPercent;

                if (was is { } old && supply.LevelPercent is { } now && now - old >= ReplacedByPoints)
                {
                    events.Add(new PrinterTelemetryEvent(at, "SupplyReplaced", $"{supply.Name}: {old}%", $"{supply.Name}: {now}%"));
                }
            }
        }

        readings.Reverse();

        return [.. readings.SelectMany(events => events)];
    }

    /// <summary>
    /// Fills in <see cref="PrinterTelemetrySample.JobsSeenSince"/> of
    /// <paramref name="current"/> and returns the spooler job that has been
    /// there longest once it is past <see cref="StuckAfter"/>, or null.
    /// </summary>
    public static (PrinterSpoolerJob Job, TimeSpan For)? StuckJob(PrinterTelemetrySample? previous, PrinterTelemetrySample current)
    {
        ArgumentNullException.ThrowIfNull(current);

        current.JobsSeenSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        (PrinterSpoolerJob Job, TimeSpan For)? stuck = null;

        foreach (var job in current.SpoolerJobs)
        {
            // The spooler's own time when it gives one; otherwise the first
            // sample the job was seen in.
            var since = job.SubmittedAt is { } submitted && submitted <= current.Timestamp
                ? submitted
                : previous is not null && previous.JobsSeenSince.TryGetValue(job.Id, out var seen) ? seen : current.Timestamp;

            current.JobsSeenSince[job.Id] = since;

            var waited = current.Timestamp - since;

            if (waited >= StuckAfter && (stuck is null || waited > stuck.Value.For))
            {
                stuck = (job, waited);
            }
        }

        return stuck;
    }

    private static string? Text(bool? value) => value switch { true => "online", false => "offline", _ => null };

    private static string? Empty(string text) => text.Length == 0 ? null : text;
}

/// <summary>Printer telemetry: the kiosk's report, and the panel's reads.</summary>
public sealed class PrinterTelemetryHandlers :
    IRequestHandler<ReportPrinterTelemetryCommand>,
    IRequestHandler<GetPrinterTelemetryQuery, IReadOnlyList<PrinterTelemetryView>>,
    IRequestHandler<GetPrinterTelemetryHistoryQuery, IReadOnlyList<PrinterTelemetryPoint>>,
    IRequestHandler<GetPrinterTelemetryEventsQuery, IReadOnlyList<PrinterTelemetryEvent>>
{
    /// <summary>The most printers one report may carry: a kiosk has a handful.</summary>
    public const int MaxPrinters = 16;

    /// <summary>A printer not heard about for this long is no longer listed.</summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromDays(7);

    /// <summary>The selected printer is reported every minute; three missed reports is stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>Other printers are reported every ten minutes.</summary>
    public static readonly TimeSpan StaleAfterWhenIdle = TimeSpan.FromMinutes(30);

    private const int MaxText = 128;

    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IPrinterTelemetryStore _store;
    private readonly IIncidentRepository _incidents;
    private readonly IncidentReporter _reporter;
    private readonly IAdminNotifier _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IClock _clock;

    public PrinterTelemetryHandlers(
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IPrinterTelemetryStore store,
        IIncidentRepository incidents,
        IncidentReporter reporter,
        IAdminNotifier admins,
        IPermissionResolver permissions,
        IClock clock)
    {
        _devices = devices;
        _kiosks = kiosks;
        _store = store;
        _incidents = incidents;
        _reporter = reporter;
        _admins = admins;
        _permissions = permissions;
        _clock = clock;
    }

    /// <inheritdoc cref="ReportPrinterTelemetryCommand" />
    public async Task Handle(ReportPrinterTelemetryCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("device");

        if (device.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("kiosk");

        var now = _clock.UtcNow;
        var samples = new List<PrinterTelemetrySample>();

        foreach (var report in request.Printers.Take(MaxPrinters))
        {
            if (Clean(report, kioskId, device.Id, now) is not { } sample
                || samples.Any(existing => existing.Printer.PrinterName == sample.Printer.PrinterName))
            {
                continue;
            }

            sample.Selected = string.Equals(kiosk.SelectedPrinter?.Name, sample.Printer.PrinterName, StringComparison.Ordinal);

            var previous = await _store.LatestAsync(kioskId, sample.Printer.PrinterName, cancellationToken).ConfigureAwait(false);
            var stuck = PrinterTelemetryChanges.StuckJob(previous, sample);
            var key = PrinterTelemetryChanges.SpoolerDedupKey(kioskId, sample.Printer.PrinterName);

            if (stuck is { } found)
            {
                await _reporter
                    .RaiseAsync(
                        IncidentType.SpoolerStuck,
                        IncidentSeverity.High,
                        UserMessage.Of(
                            "incidents.telemetry.spoolerStuck",
                            ("printer", sample.Printer.PrinterName),
                            ("jobId", found.Job.Id),
                            ("minutes", (int)found.For.TotalMinutes)),
                        key,
                        cancellationToken,
                        kioskId)
                    .ConfigureAwait(false);
            }
            else if (await _incidents.FindOpenByDedupKeyAsync(key, cancellationToken).ConfigureAwait(false) is { } open)
            {
                open.Resolve(null, new UserMessage("incidents.resolution.telemetryRecovered").Encode(), now);
                await _incidents.UpdateAsync(open, cancellationToken).ConfigureAwait(false);
            }

            samples.Add(sample);
        }

        if (samples.Count == 0)
        {
            return;
        }

        await _store.AddManyAsync(samples, cancellationToken).ConfigureAwait(false);

        await _admins
            .PrinterTelemetryUpdatedAsync(kioskId, [.. samples.Select(sample => new PrinterTelemetryView(kioskId, sample, Stale: false))], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="GetPrinterTelemetryQuery" />
    public async Task<IReadOnlyList<PrinterTelemetryView>> Handle(GetPrinterTelemetryQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var latest = await _store.LatestAsync(kiosk.Id, now - ForgottenAfter, cancellationToken).ConfigureAwait(false);

        return
        [
            .. latest
                .OrderByDescending(sample => sample.Selected)
                .ThenBy(sample => sample.Printer.PrinterName, StringComparer.OrdinalIgnoreCase)
                .Select(sample => new PrinterTelemetryView(
                    kiosk.Id, sample, now - sample.Timestamp > (sample.Selected ? StaleAfter : StaleAfterWhenIdle))),
        ];
    }

    /// <inheritdoc cref="GetPrinterTelemetryHistoryQuery" />
    public async Task<IReadOnlyList<PrinterTelemetryPoint>> Handle(
        GetPrinterTelemetryHistoryQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var (from, to) = Range(request.From, request.To, TimeSpan.FromDays(1));
        var points = Math.Clamp(request.Points, 10, 500);
        var slice = Math.Max(60, (int)Math.Ceiling((to - from).TotalSeconds / points));

        var slices = await _store.SlicesAsync(kiosk.Id, request.Printer, from, to, slice, cancellationToken).ConfigureAwait(false);

        return
        [
            .. slices.Select(sample => new PrinterTelemetryPoint(
                sample.Timestamp,
                sample.LifetimePages,
                sample.Supplies
                    .GroupBy(supply => supply.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().LevelPercent, StringComparer.Ordinal))),
        ];
    }

    /// <inheritdoc cref="GetPrinterTelemetryEventsQuery" />
    public async Task<IReadOnlyList<PrinterTelemetryEvent>> Handle(
        GetPrinterTelemetryEventsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await VisibleKioskAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);
        var (from, to) = Range(request.From, request.To, TimeSpan.FromDays(7));

        // A week of one-minute readings; a longer look is cut at the newest ones' expense never - the oldest are dropped.
        var samples = await _store.RangeAsync(kiosk.Id, request.Printer, from, to, 12_000, cancellationToken).ConfigureAwait(false);

        return PrinterTelemetryChanges.Between(samples);
    }

    private (DateTime From, DateTime To) Range(DateTime? from, DateTime? to, TimeSpan byDefault)
    {
        var end = to ?? _clock.UtcNow;
        var start = from ?? end - byDefault;

        if (start >= end)
        {
            throw new ValidationException([new ValidationError("from", "telemetry.rangeInvalid")]);
        }

        return (end - start > KioskTelemetryHandlers.LongestHistory ? end - KioskTelemetryHandlers.LongestHistory : start, end);
    }

    private async Task<Kiosk> VisibleKioskAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null || !effective.CanSee(kiosk.Id) ? throw new NotFoundException("kiosk") : kiosk;
    }

    // -----------------------------------------------------------------------
    // The kiosk is an untrusted device, and what it relays comes from a
    // printer nobody vetted: everything is bounded before it is stored.
    // -----------------------------------------------------------------------

    /// <returns>Null when the report names no printer.</returns>
    public static PrinterTelemetrySample? Clean(PrinterTelemetryReport report, Guid kioskId, Guid deviceId, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (Text(report.Name) is not { } name)
        {
            return null;
        }

        return new PrinterTelemetrySample
        {
            Timestamp = now,
            Printer = new PrinterKey { KioskId = kioskId, PrinterName = name },
            DeviceId = deviceId,
            Manufacturer = Text(report.Manufacturer),
            Model = Text(report.Model),
            SerialNumber = Text(report.SerialNumber, 64),
            Firmware = Text(report.Firmware, 64),
            DriverName = Text(report.DriverName),
            DriverVersion = Text(report.DriverVersion, 64),
            Connection = report.Connection is "Usb" or "Network" or "Other" ? report.Connection : null,
            Address = Text(report.Address, 253),
            MacAddress = Text(report.MacAddress, 32),
            Uri = Text(report.Uri, 512),
            State = report.State is "idle" or "printing" or "stopped" ? report.State : null,
            StateReasons = [.. (report.StateReasons ?? []).Select(reason => Text(reason, 64)).OfType<string>().Distinct(StringComparer.Ordinal).Take(32)],
            Online = report.Online,
            LastError = Text(report.LastError, 256),
            Supplies =
            [
                .. (report.Supplies ?? [])
                    .Where(supply => supply is not null && Text(supply.Name) is not null)
                    .Take(32)
                    .Select(supply => new PrinterSupplyLevel(
                        Text(supply.Name)!,
                        supply.Type is "toner" or "ink" or "drum" or "fuser" or "waste" or "other" ? supply.Type : "other",
                        Text(supply.Color, 32),
                        supply.LevelPercent is >= 0 and <= 100 ? supply.LevelPercent : null)),
            ],
            Trays =
            [
                .. (report.Trays ?? [])
                    .Where(tray => tray is not null && Text(tray.Name) is not null)
                    .Take(16)
                    .Select(tray => new PrinterTrayLevel(
                        Text(tray.Name)!, Text(tray.Media, 64), tray.LevelPercent is >= 0 and <= 100 ? tray.LevelPercent : null)),
            ],
            LifetimePages = report.LifetimePages is >= 0 ? report.LifetimePages : null,
            ColorPages = report.ColorPages is >= 0 ? report.ColorPages : null,
            MonoPages = report.MonoPages is >= 0 ? report.MonoPages : null,
            Features = report.Features is null
                ? null
                : new PrinterFeatureSet
                {
                    Color = report.Features.Color,
                    Duplex = report.Features.Duplex,
                    PaperSizes = Names(report.Features.PaperSizes, 64),
                    Trays = Names(report.Features.Trays, 16),
                    ResolutionDpi = report.Features.ResolutionDpi is > 0 and <= 100_000 ? report.Features.ResolutionDpi : null,
                    NativeFormats = Names(report.Features.NativeFormats, 64),
                },
            SpoolerJobs =
            [
                .. (report.SpoolerJobs ?? [])
                    .Where(job => job is not null && Text(job.Id, 128) is not null)
                    .Take(50)
                    .Select(job => new PrinterSpoolerJob(
                        Text(job.Id, 128)!,
                        job.SizeBytes is >= 0 ? job.SizeBytes : null,
                        Text(job.State, 32),
                        job.SubmittedAt is { } at && at.Year >= 2020 && at.ToUniversalTime() <= now.AddDays(1) ? at.ToUniversalTime() : null)),
            ],
        };
    }

    private static List<string> Names(IEnumerable<string>? values, int most)
        => [.. (values ?? []).Select(value => Text(value, 64)).OfType<string>().Distinct(StringComparer.Ordinal).Take(most)];

    private static string? Text(string? value, int max = MaxText)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // No control characters: this text ends up in a log line and a panel.
        var clean = new string([.. value.Trim().Where(character => !char.IsControl(character))]);

        return clean.Length == 0 ? null : clean.Length <= max ? clean : clean[..max];
    }
}
