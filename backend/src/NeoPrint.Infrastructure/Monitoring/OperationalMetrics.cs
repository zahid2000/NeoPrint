using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Storage;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Monitoring;

/// <summary>
/// The state the dashboards watch, as gauges (T-477, docs/OBSERVABILITY.md):
/// refunds and fiscal receipts not finished, payments whose refund failed,
/// storage sync objects that could not be copied, storage providers that are
/// down, open incidents and kiosks by status.
/// </summary>
/// <remarks>
/// Counted from MongoDB - the source of truth - rather than counted where
/// things happen, so a restart loses nothing and two API instances report the
/// same figure. The counts are taken once a minute by
/// <see cref="OperationalMetricsCollector"/> and only read by the exporter, so
/// a scrape never touches the database.
/// </remarks>
public sealed class OperationalMetrics : IDisposable
{
    public const string MeterName = "NeoPrint.Operations";

    private readonly Meter _meter = new(MeterName);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public OperationalMetrics()
    {
        _meter.CreateObservableGauge(
            "neoprint.refunds.open",
            () => Measure(_snapshot.Refunds, "status"),
            "{refund}",
            "Refunds not succeeded, by status");

        _meter.CreateObservableGauge(
            "neoprint.fiscal.receipts.open",
            () => Measure(_snapshot.FiscalReceipts, "status"),
            "{receipt}",
            "Fiscal receipts not issued, by status");

        _meter.CreateObservableGauge(
            "neoprint.payments.refund_failed",
            () => _snapshot.PaymentsRefundFailed,
            "{payment}",
            "Payments whose refund failed");

        _meter.CreateObservableGauge(
            "neoprint.storage.sync.failures",
            () => _snapshot.StorageSyncFailures,
            "{object}",
            "Objects the storage sync could not copy, not yet resolved");

        _meter.CreateObservableGauge(
            "neoprint.storage.providers.unhealthy",
            () => _snapshot.UnhealthyProviders,
            "{provider}",
            "Storage providers whose last health check failed");

        _meter.CreateObservableGauge(
            "neoprint.incidents.open",
            () => Measure(_snapshot.Incidents, "severity"),
            "{incident}",
            "Incidents not resolved, by severity");

        _meter.CreateObservableGauge(
            "neoprint.kiosks",
            () => Measure(_snapshot.Kiosks, "status"),
            "{kiosk}",
            "Kiosks by status");
    }

    /// <summary>When the figures were last counted; null before the first count.</summary>
    public DateTime? CountedAt => _snapshot.CountedAt;

    internal Snapshot Current => _snapshot;

    internal void Publish(Snapshot snapshot) => _snapshot = snapshot;

    public void Dispose() => _meter.Dispose();

    private static IEnumerable<Measurement<long>> Measure(IReadOnlyDictionary<string, long> counts, string tag) =>
        counts.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>(tag, pair.Key)));

    internal sealed record Snapshot(
        DateTime? CountedAt,
        IReadOnlyDictionary<string, long> Refunds,
        IReadOnlyDictionary<string, long> FiscalReceipts,
        long PaymentsRefundFailed,
        long StorageSyncFailures,
        long UnhealthyProviders,
        IReadOnlyDictionary<string, long> Incidents,
        IReadOnlyDictionary<string, long> Kiosks)
    {
        public static readonly Snapshot Empty = new(
            null,
            new Dictionary<string, long>(),
            new Dictionary<string, long>(),
            0,
            0,
            0,
            new Dictionary<string, long>(),
            new Dictionary<string, long>());
    }
}

/// <summary>
/// Counts the figures <see cref="OperationalMetrics"/> reports, once a
/// minute. Runs only when monitoring is on (<c>Monitoring:OtlpEndpoint</c>).
/// </summary>
public sealed class OperationalMetricsCollector : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly OperationalMetrics _metrics;
    private readonly ILogger<OperationalMetricsCollector> _logger;

    public OperationalMetricsCollector(
        IServiceScopeFactory scopes,
        OperationalMetrics metrics,
        ILogger<OperationalMetricsCollector> logger)
    {
        _scopes = scopes;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await CountAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The last figures stay; the next minute tries again. A
                // database that is down is the health check's to report.
                _logger.LogWarning(exception, "Counting the operational metrics failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task CountAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var refunds = await GroupAsync(
            context.Refunds.WithoutSession,
            Builders<Refund>.Filter.Ne(refund => refund.Status, RefundStatus.Succeeded),
            refund => refund.Status,
            [RefundStatus.Pending, RefundStatus.Processing, RefundStatus.Failed],
            cancellationToken).ConfigureAwait(false);

        var receipts = await GroupAsync(
            context.FiscalReceipts.WithoutSession,
            Builders<FiscalReceipt>.Filter.Ne(receipt => receipt.Status, FiscalReceiptStatus.Issued),
            receipt => receipt.Status,
            [FiscalReceiptStatus.Pending, FiscalReceiptStatus.Failed],
            cancellationToken).ConfigureAwait(false);

        var refundFailed = await context.Payments.WithoutSession
            .CountDocumentsAsync(payment => payment.Status == PaymentStatus.RefundFailed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var syncFailures = await context.StorageSyncFailures.WithoutSession
            .CountDocumentsAsync(failure => failure.ResolvedAt == null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var unhealthy = await context.StorageProviders.WithoutSession
            .CountDocumentsAsync(provider => provider.HealthStatus == StorageHealthStatus.Unhealthy, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var incidents = await GroupAsync(
            context.Incidents.WithoutSession,
            Builders<Incident>.Filter.Ne(incident => incident.Status, IncidentStatus.Resolved),
            incident => incident.Severity,
            Enum.GetValues<IncidentSeverity>(),
            cancellationToken).ConfigureAwait(false);

        var kiosks = await GroupAsync(
            context.Kiosks.WithoutSession,
            Builders<Kiosk>.Filter.Empty,
            kiosk => kiosk.Status,
            Enum.GetValues<KioskStatus>(),
            cancellationToken).ConfigureAwait(false);

        _metrics.Publish(new OperationalMetrics.Snapshot(
            DateTime.UtcNow,
            refunds,
            receipts,
            refundFailed,
            syncFailures,
            unhealthy,
            incidents,
            kiosks));
    }

    /// <summary>
    /// Counts by one enum field. Every value in <paramref name="always"/> is
    /// reported, zero included, so a series that falls back to nothing shows
    /// zero instead of disappearing from the graph.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, long>> GroupAsync<TDocument, TKey>(
        IMongoCollection<TDocument> collection,
        FilterDefinition<TDocument> filter,
        System.Linq.Expressions.Expression<Func<TDocument, TKey>> key,
        IEnumerable<TKey> always,
        CancellationToken cancellationToken)
        where TKey : struct, Enum
    {
        var groups = await collection.Aggregate()
            .Match(filter)
            .Group(key, group => new { Key = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var counts = always.ToDictionary(value => value.ToString(), _ => 0L, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            counts[group.Key.ToString()] = group.Count;
        }

        return counts;
    }
}
