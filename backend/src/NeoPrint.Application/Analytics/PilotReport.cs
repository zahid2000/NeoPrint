using System.Globalization;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Analytics;

/// <summary>Reads what the pilot report is worked out from (plan section 2.17).</summary>
public interface IPilotSourceReader
{
    /// <param name="from">The first moment, UTC, included.</param>
    /// <param name="to">The end, UTC, excluded.</param>
    /// <param name="printers">Each kiosk's selected printer, whose supply levels count.</param>
    Task<PilotSources> ReadAsync(
        DateTime from,
        DateTime to,
        IReadOnlyCollection<Guid> kiosks,
        bool includeApp,
        IReadOnlyDictionary<Guid, string> printers,
        CancellationToken cancellationToken = default);
}

/// <summary>The rolled-up days (<c>analytics_days</c>).</summary>
public interface IPilotDayRepository
{
    /// <summary>Writes each day over the one with its id: running it again changes nothing.</summary>
    Task ReplaceAsync(IReadOnlyCollection<PilotDay> days, CancellationToken cancellationToken = default);

    /// <param name="kiosks">Null: every kiosk.</param>
    Task<IReadOnlyList<PilotDay>> ListAsync(
        string from, string to, IReadOnlyCollection<Guid>? kiosks, bool includeApp, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> IdsAsync(string from, string to, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>AnalyticsRollupJob</c> (plan section 6, hourly): the last eight Baku
/// days are worked out again - a kiosk may send a week-old event - and any
/// older day still missing, a month at a time.
/// </summary>
public sealed record RunAnalyticsRollupCommand : IRequest<int>;

/// <summary>Works the days out again from the records (after a fix, or for days the job never saw).</summary>
/// <param name="From">First Baku day, included.</param>
/// <param name="To">Last Baku day, included.</param>
public sealed record RebuildPilotDaysCommand(Guid AdminUserId, DateOnly From, DateOnly To, string? Ip) : IRequest<PilotRebuildResult>;

public sealed record PilotRebuildResult(DateOnly From, DateOnly To, int Days);

/// <summary>The pilot report (plan section 2.17) for a span of Baku days and, optionally, one kiosk.</summary>
public sealed record GetPilotReportQuery(Guid AdminUserId, DateOnly From, DateOnly To, Guid? KioskId) : IRequest<PilotReportView>;

/// <summary>A reason and how often: <paramref name="Text"/> is the server's short text, null when it has none.</summary>
public sealed record PilotReasonView(string Key, string? Text, int Count);

public sealed record PilotCountView(string Key, int Count);

public sealed record PilotFormatView(string Format, int Selected, int Failed, double Share);

public sealed record PilotMethodView(string Method, int Started, int Succeeded);

/// <param name="Loss">The share of the step before that did not get here, in percent; null for the first step.</param>
public sealed record PilotStepView(string Step, int Count, double? Loss);

public sealed record PilotTotalsView(
    int Visits,
    int Orders,
    int PrintsSucceeded,
    int PrintsFailed,
    int Cancelled,
    int Refunds,
    decimal Refunded,
    decimal Sales,
    decimal Net,
    int? AveragePrintSeconds,
    int PaperSheets,
    int TestSheets,
    int TonerReplaced,
    int PaymentsStarted,
    int PaymentsSucceeded,
    int Incidents);

public sealed record PilotDayView(
    string Day, bool Live, int Visits, int Orders, int PrintsSucceeded, int PrintsFailed, decimal Net, int PaperSheets, int Incidents);

/// <param name="ComputedAt">When the oldest past day shown was worked out; null when the span is only today.</param>
/// <param name="AppFormats">The app's uploads - only for an account that sees every kiosk, without a kiosk chosen.</param>
public sealed record PilotReportView(
    DateOnly From,
    DateOnly To,
    Guid? KioskId,
    string Currency,
    DateTime? ComputedAt,
    PilotTotalsView Totals,
    IReadOnlyList<PilotStepView> Funnel,
    IReadOnlyList<PilotFormatView> Formats,
    IReadOnlyList<PilotFormatView>? AppFormats,
    IReadOnlyList<PilotReasonView> PaymentFailures,
    IReadOnlyList<PilotMethodView> Methods,
    IReadOnlyList<PilotCountView> Abandoned,
    IReadOnlyList<PilotReasonView> ConversionErrors,
    IReadOnlyList<PilotReasonView> PrintErrors,
    IReadOnlyList<PilotCountView> RefundReasons,
    IReadOnlyList<PilotCountView> Paper,
    IReadOnlyList<PilotCountView> Toner,
    IReadOnlyList<PilotCountView> Incidents,
    IReadOnlyList<PilotDayView> Days);

/// <summary>
/// The pilot report and the rollup behind it (plan sections 2.17, 6).
/// </summary>
/// <remarks>
/// Past days come from <c>analytics_days</c>; today is worked out on the
/// spot from the same records, so the page is current without waiting for
/// the hour. An account that looks after some kiosks sees only theirs.
/// </remarks>
public sealed class PilotReportHandlers :
    IRequestHandler<RunAnalyticsRollupCommand, int>,
    IRequestHandler<RebuildPilotDaysCommand, PilotRebuildResult>,
    IRequestHandler<GetPilotReportQuery, PilotReportView>
{
    /// <summary>The days the job works out again each hour: a kiosk's events may arrive a week late.</summary>
    public const int RecentDays = 8;

    /// <summary>How many missing older days one run fills in.</summary>
    public const int GapDaysPerRun = 31;

    public static readonly IReadOnlyList<string> Steps = ["selected", "previewed", "options", "paymentScreen", "paid", "printing", "printed"];

    private readonly IPilotSourceReader _sources;
    private readonly IPilotDayRepository _days;
    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<PilotReportHandlers> _logger;

    public PilotReportHandlers(
        IPilotSourceReader sources,
        IPilotDayRepository days,
        IKioskRepository kiosks,
        IPermissionResolver permissions,
        ISettingsReader settings,
        IAuditWriter audit,
        IClock clock,
        ILogger<PilotReportHandlers> logger)
    {
        _sources = sources;
        _days = days;
        _kiosks = kiosks;
        _permissions = permissions;
        _settings = settings;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="RunAnalyticsRollupCommand" />
    public async Task<int> Handle(RunAnalyticsRollupCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var today = Today();
        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);
        var written = await RollupAsync(today.AddDays(1 - RecentDays), today, kiosks, includeApp: true, cancellationToken).ConfigureAwait(false);

        // Older days with no document yet - the first run after an upgrade,
        // or a kiosk added later - as far back as the events are kept.
        var oldest = await OldestKeptAsync(today, cancellationToken).ConfigureAwait(false);
        var before = today.AddDays(-RecentDays);

        if (oldest <= before)
        {
            var present = (await _days.IdsAsync(Text(oldest), Text(before), cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            var missing = new List<DateOnly>();

            for (var day = before; day >= oldest && missing.Count < GapDaysPerRun; day = day.AddDays(-1))
            {
                if (kiosks.Any(kiosk => !present.Contains(PilotDay.IdOf(Text(day), kiosk.Id)))
                    || !present.Contains(PilotDay.IdOf(Text(day), null)))
                {
                    missing.Add(day);
                }
            }

            if (missing.Count > 0)
            {
                written += await RollupAsync(missing.Min(), missing.Max(), kiosks, includeApp: true, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Analytics rollup wrote {Days} kiosk days.", written);

        return written;
    }

    /// <inheritdoc cref="RebuildPilotDaysCommand" />
    public async Task<PilotRebuildResult> Handle(RebuildPilotDaysCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var today = Today();
        CheckSpan(request.From, request.To, today);

        var oldest = await OldestKeptAsync(today, cancellationToken).ConfigureAwait(false);

        if (request.From < oldest)
        {
            // The events behind those days are gone: working them out again
            // would write zeros over figures that were right.
            throw Invalid("from", UserMessage.Of("analytics.rebuildTooOld", ("day", oldest.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))));
        }

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).Where(kiosk => effective.CanSee(kiosk.Id)).ToList();
        var written = await RollupAsync(request.From, request.To, kiosks, effective.AllKiosks, cancellationToken).ConfigureAwait(false);

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "analytics.rebuilt",
                    "Analytics",
                    null,
                    null,
                    $"{Text(request.From)}..{Text(request.To)} ({written})",
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        return new PilotRebuildResult(request.From, request.To, written);
    }

    /// <inheritdoc cref="GetPilotReportQuery" />
    public async Task<PilotReportView> Handle(GetPilotReportQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var today = Today();
        CheckSpan(request.From, request.To, today);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var visible = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).Where(kiosk => effective.CanSee(kiosk.Id)).ToList();

        if (request.KioskId is { } one && visible.All(kiosk => kiosk.Id != one))
        {
            throw new NotFoundException("kiosk");
        }

        var kiosks = request.KioskId is { } chosen ? visible.Where(kiosk => kiosk.Id == chosen).ToList() : visible;
        var ids = kiosks.Select(kiosk => kiosk.Id).ToList();
        var includeApp = effective.AllKiosks && request.KioskId is null;
        var days = new List<PilotDay>();
        DateTime? computedAt = null;

        if (request.From < today)
        {
            var lastPast = request.To < today ? request.To : today.AddDays(-1);
            var stored = ids.Count == 0 && !includeApp
                ? []
                : await _days.ListAsync(Text(request.From), Text(lastPast), ids, includeApp, cancellationToken).ConfigureAwait(false);
            days.AddRange(stored);
            computedAt = stored.Count > 0 ? stored.Min(day => day.ComputedAt) : null;
        }

        if (request.To >= today && (ids.Count > 0 || includeApp))
        {
            days.AddRange(await ComputeAsync(today, today, kiosks, includeApp, cancellationToken).ConfigureAwait(false));
        }

        return View(request, days, Text(today), computedAt);
    }

    private async Task<int> RollupAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Kiosk> kiosks, bool includeApp, CancellationToken cancellationToken)
    {
        var days = await ComputeAsync(from, to, kiosks, includeApp, cancellationToken).ConfigureAwait(false);

        // A printer's readings are kept for a shorter time than the events
        // (docs/DATA_RETENTION.md): for a day whose readings are gone, the
        // supply figures written while they were there stay.
        var telemetryDays = await _settings.GetIntAsync(SettingKeys.KioskTelemetryRetentionDays, 30, cancellationToken).ConfigureAwait(false);
        var readingsFrom = Text(Today().AddDays(1 - telemetryDays));

        if (string.CompareOrdinal(Text(from), readingsFrom) < 0)
        {
            var kept = (await _days.ListAsync(Text(from), Text(to), null, includeApp, cancellationToken).ConfigureAwait(false))
                .ToDictionary(day => day.Id, StringComparer.Ordinal);

            foreach (var day in days.Where(day => string.CompareOrdinal(day.Day, readingsFrom) < 0))
            {
                if (kept.TryGetValue(day.Id, out var before))
                {
                    day.Toner = before.Toner;
                }
            }
        }

        await _days.ReplaceAsync(days, cancellationToken).ConfigureAwait(false);

        return days.Count;
    }

    private async Task<IReadOnlyList<PilotDay>> ComputeAsync(
        DateOnly from, DateOnly to, IReadOnlyList<Kiosk> kiosks, bool includeApp, CancellationToken cancellationToken)
    {
        var printers = kiosks
            .Where(kiosk => kiosk.SelectedPrinter is not null)
            .ToDictionary(kiosk => kiosk.Id, kiosk => kiosk.SelectedPrinter!.Name);
        var sources = await _sources
            .ReadAsync(
                PilotDayBuilder.StartOf(from),
                PilotDayBuilder.StartOf(to.AddDays(1)),
                [.. kiosks.Select(kiosk => kiosk.Id)],
                includeApp,
                printers,
                cancellationToken)
            .ConfigureAwait(false);

        return PilotDayBuilder.Build(from, to, [.. kiosks.Select(kiosk => kiosk.Id)], includeApp, sources, _clock.UtcNow);
    }

    private async Task<DateOnly> OldestKeptAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var retention = await _settings.GetIntAsync(SettingKeys.AnalyticsRetentionDays, 180, cancellationToken).ConfigureAwait(false);

        return today.AddDays(1 - Math.Max(1, retention));
    }

    private static PilotReportView View(GetPilotReportQuery request, IReadOnlyList<PilotDay> days, string today, DateTime? computedAt)
    {
        var kioskDays = days.Where(day => day.KioskId is not null).ToList();
        var funnel = kioskDays.Aggregate(PilotFunnel.Empty, (sum, day) => sum.Plus(day.Funnel));
        int[] counts = [funnel.Selected, funnel.Previewed, funnel.Options, funnel.PaymentScreen, funnel.Paid, funnel.Printing, funnel.Printed];
        var timed = kioskDays.Sum(day => day.PrintsTimed);
        var sales = kioskDays.Sum(day => day.Sales) + days.Where(day => day.KioskId is null).Sum(day => day.Sales);
        var refunded = days.Sum(day => day.Refunded);

        return new PilotReportView(
            request.From,
            request.To,
            request.KioskId,
            "AZN",
            computedAt,
            new PilotTotalsView(
                kioskDays.Sum(day => day.Visits),
                kioskDays.Sum(day => day.Orders),
                kioskDays.Sum(day => day.PrintsSucceeded),
                kioskDays.Sum(day => day.PrintsFailed),
                kioskDays.Sum(day => day.Cancelled),
                days.Sum(day => day.Refunds),
                refunded,
                sales,
                sales - refunded,
                timed > 0 ? (int)Math.Round((double)kioskDays.Sum(day => day.PrintSeconds) / timed) : null,
                kioskDays.Sum(day => day.Paper.Sum(paper => paper.Count)),
                kioskDays.Sum(day => day.TestSheets),
                kioskDays.Sum(day => day.TonerReplaced),
                days.Sum(day => day.PaymentsStarted),
                days.Sum(day => day.PaymentsSucceeded),
                kioskDays.Sum(day => day.Incidents.Sum(incident => incident.Count))),
            [
                .. Steps.Select((step, index) => new PilotStepView(
                    step,
                    counts[index],
                    index == 0 || counts[index - 1] == 0 ? null : Math.Round(100.0 * (counts[index - 1] - counts[index]) / counts[index - 1], 1))),
            ],
            Formats(kioskDays.SelectMany(day => day.Formats)),
            request.KioskId is null && days.Any(day => day.KioskId is null)
                ? Formats(days.Where(day => day.KioskId is null).SelectMany(day => day.Formats))
                : null,
            Reasons(days.SelectMany(day => day.PaymentFailures)),
            [
                .. days.SelectMany(day => day.Methods)
                    .GroupBy(method => method.Method, StringComparer.Ordinal)
                    .Select(group => new PilotMethodView(group.Key, group.Sum(m => m.Started), group.Sum(m => m.Succeeded)))
                    .OrderByDescending(method => method.Started),
            ],
            Counts(kioskDays.SelectMany(day => day.Abandoned)),
            Reasons(kioskDays.SelectMany(day => day.ConversionErrors)),
            Reasons(kioskDays.SelectMany(day => day.PrintErrors)),
            Counts(days.SelectMany(day => day.RefundReasons)),
            Counts(kioskDays.SelectMany(day => day.Paper)),
            Counts(kioskDays.SelectMany(day => day.Toner)),
            Counts(kioskDays.SelectMany(day => day.Incidents)),
            // Every date of the span, a quiet one too: a missing row would read as "not counted".
            [
                .. Enumerable.Range(0, request.To.DayNumber - request.From.DayNumber + 1)
                    .Select(offset => Text(request.From.AddDays(offset)))
                    .GroupJoin(days, date => date, day => day.Day, (date, group) => (Key: date, Days: group.ToList()), StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var kiosk = group.Days.Where(day => day.KioskId is not null).ToList();
                        return new PilotDayView(
                            group.Key,
                            group.Key == today,
                            kiosk.Sum(day => day.Visits),
                            kiosk.Sum(day => day.Orders),
                            kiosk.Sum(day => day.PrintsSucceeded),
                            kiosk.Sum(day => day.PrintsFailed),
                            group.Days.Sum(day => day.Sales - day.Refunded),
                            kiosk.Sum(day => day.Paper.Sum(paper => paper.Count)),
                            kiosk.Sum(day => day.Incidents.Sum(incident => incident.Count)));
                    }),
            ]);
    }

    private static List<PilotFormatView> Formats(IEnumerable<FormatCount> formats)
    {
        var grouped = formats
            .GroupBy(format => format.Format, StringComparer.Ordinal)
            .Select(group => (Format: group.Key, Selected: group.Sum(f => f.Selected), Failed: group.Sum(f => f.Failed)))
            .ToList();
        var total = grouped.Sum(format => format.Selected);

        return
        [
            .. grouped
                .OrderByDescending(format => format.Selected)
                .ThenBy(format => format.Format, StringComparer.Ordinal)
                .Select(format => new PilotFormatView(
                    format.Format, format.Selected, format.Failed, total == 0 ? 0 : Math.Round(100.0 * format.Selected / total, 1))),
        ];
    }

    private static List<PilotCountView> Counts(IEnumerable<NamedCount> counts)
        =>
        [
            .. counts.GroupBy(count => count.Key, StringComparer.Ordinal)
                .Select(group => new PilotCountView(group.Key, group.Sum(count => count.Count)))
                .OrderByDescending(count => count.Count)
                .ThenBy(count => count.Key, StringComparer.Ordinal),
        ];

    private static List<PilotReasonView> Reasons(IEnumerable<NamedCount> counts)
        => [.. Counts(counts).Select(count => new PilotReasonView(count.Key, ShortText(count.Key), count.Count))];

    /// <summary>The first sentence of the server's text for a key - "Bank ödənişi rədd etdi." - or null.</summary>
    private static string? ShortText(string key)
    {
        if (!Messages.Has(key))
        {
            return null;
        }

        var text = Messages.Write(key);
        var end = text.IndexOf(". ", StringComparison.Ordinal);

        return end > 0 ? text[..(end + 1)] : text;
    }

    private static void CheckSpan(DateOnly from, DateOnly to, DateOnly latest)
    {
        if (to < from || to.DayNumber - from.DayNumber >= PilotDayBuilder.MaxDays)
        {
            throw Invalid("to", UserMessage.Of("reports.periodInvalid", ("max", PilotDayBuilder.MaxDays)));
        }

        if (to > latest)
        {
            throw Invalid("to", new UserMessage("analytics.periodInFuture"));
        }
    }

    private static ValidationException Invalid(string field, UserMessage message) => new([new ValidationError(field, message)]);

    private DateOnly Today() => DateOnly.FromDateTime(BakuTime.From(_clock.UtcNow));

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
