using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Incidents;

/// <summary>What went wrong (plan section 5, <c>incidents.Type</c>).</summary>
public enum IncidentType
{
    PrinterOffline = 0,
    PaperOut = 1,
    PaperLow = 2,
    TonerLow = 3,
    PaperJam = 4,
    ConnectionLost = 5,
    AppCrash = 6,
    NoPrinterDetected = 7,
    SelectedPrinterMissing = 8,
    FiscalFailed = 9,
    RefundFailed = 10,
    StorageUnhealthy = 11,

    /// <summary>A job whose outcome nobody knows (plan section 2.5, scenario B).</summary>
    JobSuspected = 12,

    Other = 13,

    /// <summary>A kiosk's disk is nearly full (plan section 7.9).</summary>
    DiskLow = 14,

    /// <summary>A kiosk's memory has been nearly full for minutes (plan section 7.9).</summary>
    MemoryHigh = 15,

    /// <summary>A kiosk has been getting slow answers from the backend for minutes (plan section 7.9).</summary>
    BackendSlow = 16,

    /// <summary>A job has been sitting in a kiosk's system spooler for minutes (plan section 7.10).</summary>
    SpoolerStuck = 17,

    /// <summary>A kiosk's clock has been a minute or more off the server's for minutes (plan section 7.9).</summary>
    ClockOff = 18,

    /// <summary>
    /// An update did not take on a kiosk: refused, failed, or taken back after
    /// its health check (plan section 7.12, step 5; T-321).
    /// </summary>
    UpdateFailed = 19,
}

public enum IncidentSeverity
{
    Critical = 0,
    High = 1,
    Medium = 2,
}

public enum IncidentStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2,
}

/// <summary>
/// Something a person has to look at (plan sections 5, 8A.3 "Incident-lər").
/// </summary>
/// <remarks>
/// <see cref="DedupKey"/> is unique while it matters: the same failure seen
/// again - a retried consumer, the next run of a job - does not open a second
/// incident (<c>refund-failed:{refundId}</c>, <c>fiscal-pending:{receiptId}</c>).
/// </remarks>
public sealed class Incident : Entity
{
    public Guid? KioskId { get; set; }

    public IncidentType Type { get; set; }

    public IncidentSeverity Severity { get; set; }

    public IncidentStatus Status { get; private set; } = IncidentStatus.Open;

    /// <summary>Not resolved yet. Stored for the unique index: one open incident per <see cref="DedupKey"/>.</summary>
    public bool Open { get; private set; } = true;

    /// <summary>A stored message saying what happened; never a secret or personal data.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>What it is about: <c>Refund</c>, <c>FiscalReceipt</c>, <c>PrintJob</c>, <c>Wallet</c>.</summary>
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }

    public string DedupKey { get; set; } = string.Empty;

    public DateTime? AcknowledgedAt { get; private set; }

    public Guid? AcknowledgedBy { get; private set; }

    public DateTime? ResolvedAt { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public string? Resolution { get; private set; }


    public static Incident Raise(
        IncidentType type,
        IncidentSeverity severity,
        UserMessage details,
        string dedupKey,
        DateTime now,
        Guid? kioskId = null,
        string? entityType = null,
        Guid? entityId = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        return new Incident
        {
            Type = type,
            Severity = severity,
            Details = details.Encode(),
            DedupKey = dedupKey,
            KioskId = kioskId,
            EntityType = entityType,
            EntityId = entityId,
            CreatedAt = now,
        };
    }

    public void Acknowledge(Guid adminId, DateTime now)
    {
        if (Status != IncidentStatus.Open)
        {
            return;
        }

        Status = IncidentStatus.Acknowledged;
        AcknowledgedBy = adminId;
        AcknowledgedAt = now;
    }

    /// <summary>Closed with a note (plan section 8A.1: mandatory for incident resolution).</summary>
    /// <param name="adminId">Null when the system closed it: the cause went away.</param>
    public void Resolve(Guid? adminId, string resolution, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolution);

        if (Status == IncidentStatus.Resolved)
        {
            return;
        }

        Status = IncidentStatus.Resolved;
        Open = false;
        ResolvedBy = adminId;
        ResolvedAt = now;
        Resolution = resolution;
    }
}

/// <summary>
/// That an incident was sent to one person over one channel
/// (<c>incident_notifications</c>): an event delivered twice tells nobody
/// twice. Who, by account id - no address and no chat id is kept.
/// </summary>
/// <remarks>
/// A row of its own rather than a list on the incident: the system writes
/// these while the incident is being acknowledged, or is closing by itself,
/// and those must not wait for - or lose to - a note about an e-mail.
/// </remarks>
public sealed class IncidentNotification
{
    /// <summary><see cref="IdOf"/>: unique, so the same mark cannot be written twice.</summary>
    public string Id { get; set; } = string.Empty;

    public Guid IncidentId { get; set; }

    public DateTime SentAt { get; set; }

    public static string IdOf(Guid incidentId, string channel, Guid adminId) => $"{incidentId:N}:{channel}:{adminId:N}";

    public static IncidentNotification Of(Guid incidentId, string channel, Guid adminId, DateTime sentAt)
        => new() { Id = IdOf(incidentId, channel, adminId), IncidentId = incidentId, SentAt = sentAt };
}
