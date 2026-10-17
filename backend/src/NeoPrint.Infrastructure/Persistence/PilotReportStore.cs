using MongoDB.Driver;

using NeoPrint.Application.Analytics;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Reads, for the pilot report, what each module wrote (plan section 2.17).
/// Read only - the report changes nothing it reads.
/// </summary>
public sealed class MongoPilotSourceReader : IPilotSourceReader
{
    /// <summary>A printer's level is read in slices of this many seconds: enough to see it go down.</summary>
    private const int LevelSliceSeconds = 900;

    private readonly MongoContext _context;
    private readonly IPrinterTelemetryStore _telemetry;

    public MongoPilotSourceReader(MongoContext context, IPrinterTelemetryStore telemetry)
    {
        _context = context;
        _telemetry = telemetry;
    }

    public async Task<PilotSources> ReadAsync(
        DateTime from,
        DateTime to,
        IReadOnlyCollection<Guid> kiosks,
        bool includeApp,
        IReadOnlyDictionary<Guid, string> printers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kiosks);
        ArgumentNullException.ThrowIfNull(printers);

        // A visit that began the evening before, an order paid the morning
        // after: the day around the span is read too, and the builder keeps
        // only what falls inside it.
        var before = from.AddDays(-1);
        var after = to.AddDays(1);
        var nullableKiosks = kiosks.Select(id => (Guid?)id).ToList();

        var events = await _context.AnalyticsEvents
            .Find(e => e.Source == AnalyticsSource.Kiosk && nullableKiosks.Contains(e.KioskId) && e.Timestamp >= before && e.Timestamp < after)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var jobs = await _context.PrintJobs
            .Find(job => kiosks.Contains(job.KioskId) && job.CreatedAt >= before && job.CreatedAt < after)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var paymentFilter = Builders<Payment>.Filter.Eq(p => p.Purpose, PaymentPurpose.PrintJob)
                            & (Builders<Payment>.Filter.Gte(p => p.CreatedAt, from) & Builders<Payment>.Filter.Lt(p => p.CreatedAt, to)
                               | Builders<Payment>.Filter.Gte(p => p.PaidAt, from) & Builders<Payment>.Filter.Lt(p => p.PaidAt, to));
        var payments = (await _context.Payments.Find(paymentFilter).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(p => Counted(p.KioskId, kiosks, includeApp))
            .ToList();

        var refunds = await _context.Refunds
            .Find(r => r.Status == RefundStatus.Succeeded && r.CompletedAt >= from && r.CompletedAt < to)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var refundPaymentIds = refunds.Select(r => r.PaymentId).Distinct().ToList();
        var refundPayments = (await _context.Payments.Find(p => refundPaymentIds.Contains(p.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        var attempts = await _context.PrintAttempts
            .Find(a => kiosks.Contains(a.KioskId) && a.UpdatedAt >= from && a.UpdatedAt < after)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var cancelledIds = (await _context.StateTransitions
                .Find(t => t.EntityType == nameof(PrintJob) && t.ToStatus == nameof(PrintJobStatus.Cancelled) && t.CreatedAt >= from && t.CreatedAt < to)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(t => (t.EntityId, t.CreatedAt))
            .ToList();
        var cancelledJobIds = cancelledIds.Select(t => t.EntityId).Distinct().ToList();
        var cancelledKiosks = (await _context.PrintJobs
                .Find(job => cancelledJobIds.Contains(job.Id) && kiosks.Contains(job.KioskId))
                .Project(job => new { job.Id, job.KioskId })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(job => job.Id, job => job.KioskId);

        var supplies = await _context.SupplyEvents
            .Find(s => kiosks.Contains(s.KioskId) && s.At >= from && s.At < to)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var levels = new List<PilotLevelRow>();

        foreach (var (kiosk, printer) in printers)
        {
            if (!kiosks.Contains(kiosk))
            {
                continue;
            }

            foreach (var sample in await _telemetry.SlicesAsync(kiosk, printer, before, to, LevelSliceSeconds, cancellationToken).ConfigureAwait(false))
            {
                levels.AddRange(sample.Supplies
                    .Where(supply => supply.LevelPercent is not null)
                    .Select(supply => new PilotLevelRow(kiosk, sample.Timestamp, supply.Name, supply.LevelPercent!.Value)));
            }
        }

        var incidents = await _context.Incidents
            .Find(i => nullableKiosks.Contains(i.KioskId) && i.CreatedAt >= from && i.CreatedAt < to)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var files = includeApp
            ? await _context.Files
                .Find(f => f.CreatedAt >= from && f.CreatedAt < to && (f.Status == FileStatus.Ready || f.Status == FileStatus.Failed))
                .Project(f => new { f.CreatedAt, f.Extension, f.Status, f.FailureReason })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false)
            : [];

        return new PilotSources(
            [
                .. events.Where(e => e.KioskId is not null)
                    .Select(e => new PilotEventRow(e.Timestamp, e.KioskId!.Value, e.SessionId, e.Event, e.Props)),
            ],
            [
                .. jobs.Select(job => new PilotJobRow(
                    job.Id, job.KioskId, job.JobCode, job.CreatedAt, job.Status, job.PaidAt, job.PaymentSkipped, job.StartedAt)),
            ],
            [
                .. payments.Select(p => new PilotPaymentRow(
                    p.KioskId,
                    p.CreatedAt,
                    p.PaidAt,
                    p.Status,
                    p.Method,
                    p.FailureReason is { Length: > 0 } reason ? UserMessage.Decode(reason).Key : null,
                    (p.Parts?.Money ?? p.Amount).Amount)),
            ],
            [
                .. refunds
                    .Select(r => (Refund: r, Payment: refundPayments.GetValueOrDefault(r.PaymentId)))
                    .Where(pair => pair.Payment is { Purpose: PaymentPurpose.PrintJob } && Counted(pair.Payment.KioskId, kiosks, includeApp))
                    .Select(pair => new PilotRefundRow(pair.Payment!.KioskId, pair.Refund.CompletedAt!.Value, pair.Refund.Amount.Amount, pair.Refund.Reason.ToString())),
            ],
            [
                .. attempts.Select(a => new PilotAttemptRow(
                    a.KioskId, a.FinishedAt ?? a.UpdatedAt, a.Status, a.ErrorCode, a.StartedAt, a.FinishedAt, a.ReportedSheets)),
            ],
            [
                .. cancelledIds
                    .Where(t => cancelledKiosks.ContainsKey(t.EntityId))
                    .Select(t => new PilotCancelRow(cancelledKiosks[t.EntityId], t.CreatedAt)),
            ],
            [.. supplies.Select(s => new PilotSupplyRow(s.KioskId, s.At, s.Resource, s.Type, s.Delta))],
            levels,
            [.. incidents.Select(i => new PilotIncidentRow(i.KioskId, i.CreatedAt, i.Severity))],
            [
                .. files.Select(f => new PilotFileRow(
                    f.CreatedAt,
                    f.Extension,
                    f.Status == FileStatus.Failed,
                    f.FailureReason is { Length: > 0 } reason ? UserMessage.Decode(reason).Key : null)),
            ]);
    }

    /// <summary>A kiosk's record counts for its kiosk; one with no kiosk, only with the app's day.</summary>
    private static bool Counted(Guid? kioskId, IReadOnlyCollection<Guid> kiosks, bool includeApp)
        => kioskId is { } id ? kiosks.Contains(id) : includeApp;
}

/// <inheritdoc cref="IPilotDayRepository" />
public sealed class PilotDayRepository : IPilotDayRepository
{
    private readonly MongoContext _context;

    public PilotDayRepository(MongoContext context) => _context = context;

    public async Task ReplaceAsync(IReadOnlyCollection<PilotDay> days, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(days);

        if (days.Count == 0)
        {
            return;
        }

        await _context.AnalyticsDays
            .BulkWriteAsync(
                days.Select(day => new ReplaceOneModel<PilotDay>(Builders<PilotDay>.Filter.Eq(d => d.Id, day.Id), day) { IsUpsert = true }),
                new BulkWriteOptions { IsOrdered = false },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PilotDay>> ListAsync(
        string from, string to, IReadOnlyCollection<Guid>? kiosks, bool includeApp, CancellationToken cancellationToken = default)
    {
        var filter = Builders<PilotDay>.Filter.Gte(d => d.Day, from) & Builders<PilotDay>.Filter.Lte(d => d.Day, to);

        if (kiosks is not null)
        {
            var allowed = kiosks.Select(id => (Guid?)id).ToList();

            if (includeApp)
            {
                allowed.Add(null);
            }

            filter &= Builders<PilotDay>.Filter.In(d => d.KioskId, allowed);
        }
        else if (!includeApp)
        {
            filter &= Builders<PilotDay>.Filter.Ne(d => d.KioskId, null);
        }

        return await _context.AnalyticsDays.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> IdsAsync(string from, string to, CancellationToken cancellationToken = default)
        => await _context.AnalyticsDays
            .Find(Builders<PilotDay>.Filter.Gte(d => d.Day, from) & Builders<PilotDay>.Filter.Lte(d => d.Day, to))
            .Project(d => d.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
