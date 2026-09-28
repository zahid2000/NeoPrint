namespace NeoPrint.Contracts;

/// <summary>A job joined a kiosk's queue (plan section 6, events).</summary>
public sealed record PrintJobQueued(Guid JobId, Guid KioskId, string? CorrelationId);

/// <summary>A job's status changed.</summary>
public sealed record PrintJobStatusChanged(Guid JobId, Guid KioskId, string From, string To, string? CorrelationId);

/// <summary>
/// A job printed. <paramref name="Paper"/> and <paramref name="PrintedSheets"/>
/// are what the paper counter goes down by (plan section 2.15).
/// </summary>
public sealed record PrintJobCompleted(Guid JobId, Guid KioskId, string Paper, int PrintedSheets, string? CorrelationId);

/// <summary>
/// A job stopped part-way; the sheets already used still come off the counter.
/// </summary>
public sealed record PrintJobInterrupted(Guid JobId, Guid KioskId, string Paper, int PrintedSheets, string? CorrelationId);
