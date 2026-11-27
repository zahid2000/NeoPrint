using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Driver;

using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Monitoring;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The gauges the dashboards read (T-477): counted from MongoDB, every status
/// reported - zero included - and read by a listener the way the exporter does.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OperationalMetricsTests
{
    private readonly NeoPrintApiFixture _fixture;

    public OperationalMetricsTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_gauges_report_what_the_database_holds_with_every_status_present()
    {
        if (_fixture.Skipped)
        {
            return;
        }

        using var metrics = new OperationalMetrics();
        var collector = new OperationalMetricsCollector(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            metrics,
            NullLogger<OperationalMetricsCollector>.Instance);

        metrics.CountedAt.Should().BeNull("nothing is reported before the first count");

        await collector.CountAsync(CancellationToken.None);

        long kiosks;
        long pendingRefunds;
        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            kiosks = await context.Kiosks.WithoutSession.CountDocumentsAsync(FilterDefinition<Kiosk>.Empty);
            pendingRefunds = await context.Refunds.WithoutSession.CountDocumentsAsync(refund => refund.Status == RefundStatus.Pending);
        }

        metrics.CountedAt.Should().NotBeNull();

        var seen = Read();
        seen.Should().ContainKey(("neoprint.kiosks", "Online"), "a status with no kiosk is reported as zero, not left out");
        seen.Where(pair => pair.Key.Name == "neoprint.kiosks").Sum(pair => pair.Value).Should().Be(kiosks);
        seen[("neoprint.refunds.open", "Pending")].Should().Be(pendingRefunds);
        seen.Should().ContainKey(("neoprint.refunds.open", "Failed"));
        seen.Should().NotContainKey(("neoprint.refunds.open", "Succeeded"), "a finished refund is not open");
        seen.Should().ContainKey(("neoprint.fiscal.receipts.open", "Failed"));
        seen.Should().ContainKey(("neoprint.storage.sync.failures", string.Empty));
        seen.Should().ContainKey(("neoprint.payments.refund_failed", string.Empty));
    }

    /// <summary>Every measurement of the operations meter, by name and its one tag's value.</summary>
    private static Dictionary<(string Name, string Tag), long> Read()
    {
        var seen = new Dictionary<(string Name, string Tag), long>();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OperationalMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tag = tags.Length > 0 ? tags[0].Value?.ToString() ?? string.Empty : string.Empty;
            seen[(instrument.Name, tag)] = value;
        });

        listener.Start();
        listener.RecordObservableInstruments();

        return seen;
    }
}
