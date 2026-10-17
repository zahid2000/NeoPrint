using System.Diagnostics.Metrics;

namespace NeoPrint.Application.Common;

/// <summary>
/// The application's own counters (docs/OBSERVABILITY.md, "Metrics"), on the
/// runtime's <see cref="Meter"/> - no package. Any listener that reads the
/// <c>NeoPrint</c> meter sees them (<c>dotnet-counters monitor -n NeoPrint.Api --counters NeoPrint</c>
/// today, an OpenTelemetry exporter once one is approved). The admin panel
/// shows the same figures from what the jobs store ("Arxa plan işləri").
/// </summary>
public static class NeoPrintMetrics
{
    public const string MeterName = "NeoPrint";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Kiosk analytics events that were not kept, by <c>reason</c>.</summary>
    public static readonly Counter<long> AnalyticsEventsRejected =
        Meter.CreateCounter<long>("neoprint.analytics.events_rejected", "{event}", "Kiosk analytics events that were not kept");

    /// <summary>Objects the file cleanup removed from storage.</summary>
    public static readonly Counter<long> FilesCleanedUp =
        Meter.CreateCounter<long>("neoprint.files.cleanup.deleted", "{object}", "Storage objects removed by the file cleanup");

    /// <summary>Bytes the file cleanup freed.</summary>
    public static readonly Counter<long> FilesCleanedUpBytes =
        Meter.CreateCounter<long>("neoprint.files.cleanup.deleted_bytes", "By", "Bytes freed by the file cleanup");

    /// <summary>Messages found in dead-letter queues at a check, by <c>queue</c>.</summary>
    public static readonly Histogram<long> DeadLetterMessages =
        Meter.CreateHistogram<long>("neoprint.messaging.dead_letter_messages", "{message}", "Messages in a dead-letter queue when it was checked");

    /// <summary>Findings of the money checks, by <c>check</c> and <c>kind</c>.</summary>
    public static readonly Counter<long> ReconciliationFindings =
        Meter.CreateCounter<long>("neoprint.reconciliation.findings", "{finding}", "Mismatches the wallet and payment checks found");
}
