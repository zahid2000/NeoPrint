using System.Globalization;

using NeoPrint.Application.Common;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.Analytics;

/// <summary>A kiosk's analytics event, as the rollup reads it.</summary>
public sealed record PilotEventRow(DateTime At, Guid KioskId, string SessionId, string Event, IReadOnlyDictionary<string, string> Props);

/// <summary>A print job: where its order got to.</summary>
public sealed record PilotJobRow(
    Guid Id, Guid KioskId, string JobCode, DateTime CreatedAt, PrintJobStatus Status, DateTime? PaidAt, bool PaymentSkipped, DateTime? StartedAt);

/// <summary>A print payment (not a top-up).</summary>
/// <param name="Money">The wallet and card or bank part - what the revenue report counts.</param>
/// <param name="FailureKey">The message key of the bank's refusal, when it refused.</param>
public sealed record PilotPaymentRow(
    Guid? KioskId, DateTime CreatedAt, DateTime? PaidAt, PaymentStatus Status, PaymentMethod Method, string? FailureKey, decimal Money);

/// <summary>A completed refund of a print payment, at the payment's kiosk.</summary>
public sealed record PilotRefundRow(Guid? KioskId, DateTime CompletedAt, decimal Amount, string Reason);

/// <summary>One time a job went to the printer, as the kiosk reported it.</summary>
public sealed record PilotAttemptRow(
    Guid KioskId, DateTime At, string Status, string? ErrorCode, DateTime? StartedAt, DateTime? FinishedAt, int Sheets);

/// <summary>A job that was cancelled, and when.</summary>
public sealed record PilotCancelRow(Guid KioskId, DateTime At);

/// <summary>A move of a kiosk's paper or toner counter (<c>supply_events</c>).</summary>
public sealed record PilotSupplyRow(Guid KioskId, DateTime At, string Resource, SupplyEventType Type, int Delta);

/// <summary>A level the kiosk's selected printer reported for one of its supplies.</summary>
public sealed record PilotLevelRow(Guid KioskId, DateTime At, string Supply, int Level);

public sealed record PilotIncidentRow(Guid? KioskId, DateTime At, IncidentSeverity Severity);

/// <summary>A file a customer uploaded in the app: its format and whether it opened.</summary>
public sealed record PilotFileRow(DateTime At, string Extension, bool Failed, string? FailureKey);

/// <summary>
/// Everything the rollup reads for a span of days. The analytics events and
/// the printer levels start a day early: a visit that began before midnight
/// belongs to the day before, and a level needs the reading before it.
/// </summary>
public sealed record PilotSources(
    IReadOnlyList<PilotEventRow> Events,
    IReadOnlyList<PilotJobRow> Jobs,
    IReadOnlyList<PilotPaymentRow> Payments,
    IReadOnlyList<PilotRefundRow> Refunds,
    IReadOnlyList<PilotAttemptRow> Attempts,
    IReadOnlyList<PilotCancelRow> Cancellations,
    IReadOnlyList<PilotSupplyRow> Supplies,
    IReadOnlyList<PilotLevelRow> Levels,
    IReadOnlyList<PilotIncidentRow> Incidents,
    IReadOnlyList<PilotFileRow> Files);

/// <summary>
/// Works out the pilot report's day figures from the records (plan section
/// 2.17, <c>AnalyticsRollupJob</c>). No reads, no clock: the same records
/// give the same days, which is what makes the rollup safe to run again.
/// </summary>
/// <remarks>
/// <para>
/// A day is a Baku day (plan section 2.1: what people read is Baku time):
/// 00:00 in Baku is 20:00 UTC the day before.
/// </para>
/// <para>
/// The way through a kiosk (the funnel) follows one visit, under its session
/// id, from the file to the printed sheet; the visit counts on the day it
/// began. The steps after the order are read from the order itself - paid,
/// sent to the printer, printed - not from the kiosk's own events, which a
/// kiosk long offline may never send.
/// </para>
/// </remarks>
public static class PilotDayBuilder
{
    /// <summary>The longest span the report and the rollup take.</summary>
    public const int MaxDays = 366;

    /// <summary>The message key for a payment whose time ran out (it has no failure reason of its own).</summary>
    public const string ExpiredKey = "analytics.paymentExpired";

    /// <summary>The message key for a refused payment that kept no reason.</summary>
    public const string DeclinedKey = "payments.declined";

    /// <summary>The message key for a stopped print that kept no reason.</summary>
    public const string PrinterErrorKey = "printer.error";

    public static string DayOf(DateTime utc) => DateOnly.FromDateTime(BakuTime.From(utc)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The UTC moment a Baku day starts.</summary>
    public static DateTime StartOf(DateOnly day)
        => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), BakuTime.Zone);

    /// <summary>
    /// A day for each kiosk and each date from <paramref name="from"/> to
    /// <paramref name="to"/> - an empty day too, so that a day recomputed to
    /// nothing replaces what it said before - and, with
    /// <paramref name="includeApp"/>, the app's uploads for each date.
    /// </summary>
    public static IReadOnlyList<PilotDay> Build(
        DateOnly from, DateOnly to, IReadOnlyCollection<Guid> kiosks, bool includeApp, PilotSources sources, DateTime computedAt)
    {
        ArgumentNullException.ThrowIfNull(kiosks);
        ArgumentNullException.ThrowIfNull(sources);

        var days = new Dictionary<string, PilotDay>(StringComparer.Ordinal);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var kiosk in kiosks)
            {
                days[PilotDay.IdOf(day, kiosk)] = New(day, kiosk, computedAt);
            }

            if (includeApp)
            {
                days[PilotDay.IdOf(day, null)] = New(day, null, computedAt);
            }
        }

        PilotDay? Find(DateTime at, Guid? kiosk) => days.GetValueOrDefault(PilotDay.IdOf(DayOf(at), kiosk));

        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);

        Tally Of(PilotDay day)
        {
            if (!tallies.TryGetValue(day.Id, out var tally))
            {
                tally = new Tally();
                tallies[day.Id] = tally;
            }

            return tally;
        }

        Visits(sources, Find, Of);

        foreach (var payment in sources.Payments)
        {
            if (Find(payment.CreatedAt, payment.KioskId) is { } started)
            {
                var tally = Of(started);
                started.PaymentsStarted++;
                var paid = payment.PaidAt is not null;
                tally.Methods.Add(payment.Method.ToString(), paid);

                if (paid)
                {
                    started.PaymentsSucceeded++;
                }
                else if (payment.Status == PaymentStatus.Failed)
                {
                    tally.PaymentFailures.Add(payment.FailureKey ?? DeclinedKey);
                }
                else if (payment.Status == PaymentStatus.Expired)
                {
                    tally.PaymentFailures.Add(ExpiredKey);
                }
            }

            if (payment.PaidAt is { } paidAt && Find(paidAt, payment.KioskId) is { } sold)
            {
                sold.Sales += payment.Money;
            }
        }

        foreach (var refund in sources.Refunds)
        {
            if (Find(refund.CompletedAt, refund.KioskId) is { } day)
            {
                day.Refunds++;
                day.Refunded += refund.Amount;
                Of(day).RefundReasons.Add(refund.Reason);
            }
        }

        foreach (var job in sources.Jobs)
        {
            if (job.Status != PrintJobStatus.Draft && Find(job.CreatedAt, job.KioskId) is { } day)
            {
                day.Orders++;
            }
        }

        foreach (var attempt in sources.Attempts)
        {
            if (Find(attempt.At, attempt.KioskId) is not { } day)
            {
                continue;
            }

            if (attempt.Status == nameof(PrintJobStatus.Completed))
            {
                day.PrintsSucceeded++;

                if (attempt is { StartedAt: { } started, FinishedAt: { } finished } && finished >= started)
                {
                    day.PrintsTimed++;
                    day.PrintSeconds += (long)(finished - started).TotalSeconds;
                }
            }
            else if (attempt.Status == nameof(PrintJobStatus.Interrupted))
            {
                day.PrintsFailed++;
                Of(day).PrintErrors.Add(string.IsNullOrWhiteSpace(attempt.ErrorCode) ? PrinterErrorKey : attempt.ErrorCode);
            }
        }

        foreach (var cancelled in sources.Cancellations)
        {
            if (Find(cancelled.At, cancelled.KioskId) is { } day)
            {
                day.Cancelled++;
            }
        }

        foreach (var supply in sources.Supplies)
        {
            if (Find(supply.At, supply.KioskId) is not { } day)
            {
                continue;
            }

            switch (supply.Type)
            {
                case SupplyEventType.JobPrinted when supply.Delta < 0:
                    Of(day).Paper.Add(PaperOf(supply.Resource), -supply.Delta);
                    break;

                case SupplyEventType.TestPrint when supply.Delta < 0:
                    day.TestSheets -= supply.Delta;
                    break;

                case SupplyEventType.Replaced when !supply.Resource.StartsWith("Paper", StringComparison.Ordinal):
                    day.TonerReplaced++;
                    break;
            }
        }

        foreach (var series in sources.Levels.GroupBy(level => (level.KioskId, level.Supply)))
        {
            PilotLevelRow? before = null;

            foreach (var level in series.OrderBy(level => level.At))
            {
                if (before is not null && level.Level < before.Level && Find(level.At, level.KioskId) is { } day)
                {
                    Of(day).Toner.Add(level.Supply, before.Level - level.Level);
                }

                before = level;
            }
        }

        foreach (var incident in sources.Incidents)
        {
            if (Find(incident.At, incident.KioskId) is { } day)
            {
                Of(day).Incidents.Add(incident.Severity.ToString());
            }
        }

        foreach (var file in sources.Files)
        {
            if (Find(file.At, null) is { } day)
            {
                var tally = Of(day);
                tally.Formats.Add(Format(file.Extension), file.Failed);

                if (file.Failed)
                {
                    tally.ConversionErrors.Add(file.FailureKey ?? "files.conversionFailed");
                }
            }
        }

        foreach (var day in days.Values)
        {
            if (tallies.TryGetValue(day.Id, out var tally))
            {
                tally.WriteTo(day);
            }
        }

        return [.. days.Values.OrderBy(day => day.Day, StringComparer.Ordinal).ThenBy(day => day.Id, StringComparer.Ordinal)];
    }

    private static PilotDay New(string day, Guid? kiosk, DateTime computedAt)
        => new() { Id = PilotDay.IdOf(day, kiosk), Day = day, KioskId = kiosk, ComputedAt = computedAt };

    /// <summary>
    /// The visits: each session's first event dates it; its file, its
    /// screens and its order say how far it got.
    /// </summary>
    private static void Visits(PilotSources sources, Func<DateTime, Guid?, PilotDay?> find, Func<PilotDay, Tally> of)
    {
        var jobs = sources.Jobs
            .GroupBy(job => (job.KioskId, job.JobCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(job => job.CreatedAt).First());

        // Print results go under "job-<code>", not under the visit (docs/DECISIONS.md 2026-09-28).
        foreach (var session in sources.Events
                     .Where(e => !e.SessionId.StartsWith("job-", StringComparison.Ordinal))
                     .GroupBy(e => (e.KioskId, e.SessionId)))
        {
            var events = session.OrderBy(e => e.At).ToList();

            if (find(events[0].At, session.Key.KioskId) is not { } day)
            {
                continue;
            }

            day.Visits++;

            var selected = events.Any(e => e.Event is "file_selected" or "conversion_failed");
            var previewed = events.Any(e => e.Event == "file_selected");
            var options = events.Any(e => e.Event == "screen_view" && e.Props.GetValueOrDefault("screen") == "Options");
            var order = events.LastOrDefault(e => e.Event == "order_placed");
            var job = order?.Props.GetValueOrDefault("jobCode") is { } code ? jobs.GetValueOrDefault((session.Key.KioskId, code)) : null;
            var paid = job is not null && (job.PaidAt is not null || job.PaymentSkipped);
            var printing = job is { StartedAt: not null } || job?.Status is PrintJobStatus.Printing or PrintJobStatus.Completed or PrintJobStatus.Interrupted;
            var printed = job?.Status == PrintJobStatus.Completed;

            // Each step only after the one before: a visit that skipped the
            // options screen (a flash order straight from the preview) still
            // ordered, but the funnel shows the way most visits take.
            bool[] steps = [selected, previewed, options, order is not null, paid, printing, printed];
            var reached = 0;

            while (reached < steps.Length && steps[reached])
            {
                reached++;
            }

            day.Funnel = day.Funnel.Plus(new PilotFunnel(
                reached > 0 ? 1 : 0,
                reached > 1 ? 1 : 0,
                reached > 2 ? 1 : 0,
                reached > 3 ? 1 : 0,
                reached > 4 ? 1 : 0,
                reached > 5 ? 1 : 0,
                reached > 6 ? 1 : 0));

            var tally = of(day);

            foreach (var e in events)
            {
                switch (e.Event)
                {
                    case "file_selected":
                        tally.Formats.Add(Format(e.Props.GetValueOrDefault("format")), false);
                        break;

                    case "conversion_failed":
                        tally.Formats.Add(Format(e.Props.GetValueOrDefault("format")), true);
                        tally.ConversionErrors.Add(e.Props.GetValueOrDefault("error") ?? "conversion.failed");
                        break;

                    case "flow_abandoned":
                        tally.Abandoned.Add(e.Props.GetValueOrDefault("lastScreen") ?? "Unknown");
                        break;
                }
            }
        }
    }

    private static string Format(string? extension)
        => string.IsNullOrWhiteSpace(extension) ? "?" : extension.Trim().TrimStart('.').ToLowerInvariant();

    private static string PaperOf(string resource)
        => resource.StartsWith("Paper", StringComparison.Ordinal) ? resource["Paper".Length..] : resource;

    private sealed class Counter
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Add(string key, int count = 1) => _counts[key] = _counts.GetValueOrDefault(key) + count;

        public List<NamedCount> ToList()
            => [.. _counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new NamedCount(pair.Key, pair.Value))];
    }

    private sealed class Tally
    {
        private readonly Dictionary<string, (int Selected, int Failed)> _formats = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Started, int Succeeded)> _methods = new(StringComparer.Ordinal);

        public Counter Abandoned { get; } = new();

        public Counter ConversionErrors { get; } = new();

        public Counter PaymentFailures { get; } = new();

        public Counter RefundReasons { get; } = new();

        public Counter PrintErrors { get; } = new();

        public Counter Paper { get; } = new();

        public Counter Toner { get; } = new();

        public Counter Incidents { get; } = new();

        public FormatTally Formats => new(_formats);

        public MethodTally Methods => new(_methods);

        public void WriteTo(PilotDay day)
        {
            day.Abandoned = Abandoned.ToList();
            day.ConversionErrors = ConversionErrors.ToList();
            day.PaymentFailures = PaymentFailures.ToList();
            day.RefundReasons = RefundReasons.ToList();
            day.PrintErrors = PrintErrors.ToList();
            day.Paper = Paper.ToList();
            day.Toner = Toner.ToList();
            day.Incidents = Incidents.ToList();
            day.Formats =
            [
                .. _formats.OrderByDescending(pair => pair.Value.Selected).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new FormatCount(pair.Key, pair.Value.Selected, pair.Value.Failed)),
            ];
            day.Methods =
            [
                .. _methods.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new MethodCount(pair.Key, pair.Value.Started, pair.Value.Succeeded)),
            ];
        }
    }

    private readonly struct FormatTally(Dictionary<string, (int Selected, int Failed)> formats)
    {
        public void Add(string format, bool failed)
        {
            var (selected, failures) = formats.GetValueOrDefault(format);
            formats[format] = (selected + 1, failures + (failed ? 1 : 0));
        }
    }

    private readonly struct MethodTally(Dictionary<string, (int Started, int Succeeded)> methods)
    {
        public void Add(string method, bool succeeded)
        {
            var (started, done) = methods.GetValueOrDefault(method);
            methods[method] = (started + 1, done + (succeeded ? 1 : 0));
        }
    }
}
