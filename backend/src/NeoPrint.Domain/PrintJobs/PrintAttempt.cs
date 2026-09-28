namespace NeoPrint.Domain.PrintJobs;

/// <summary>
/// One try at printing a job, as the kiosk reported it (plan sections 2.5,
/// 4 <c>print_attempts</c>).
/// </summary>
/// <remarks>
/// <para>
/// A record of evidence, not a state machine: it is what the admin reads when
/// nobody knows whether a job printed - which printer, the spooler's reference,
/// and the printer's own page counter before and after.
/// </para>
/// <para>
/// One row per job and attempt number; a report sent again (a lost answer)
/// overwrites the row instead of adding one.
/// </para>
/// </remarks>
public sealed class PrintAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }

    public int AttemptNo { get; set; }

    public Guid KioskId { get; set; }

    public string? PrinterName { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>The spooler's reference: the CUPS request id, the Windows document name.</summary>
    public string? SpoolerJobId { get; set; }

    public long? PrinterCounterBefore { get; set; }

    public long? PrinterCounterAfter { get; set; }

    /// <summary>"sheets" or "impressions": what the counter counts.</summary>
    public string? CounterUnit { get; set; }

    public int ReportedSides { get; set; }

    public int ReportedSheets { get; set; }

    /// <summary>The kiosk's last report for this attempt: Printing, Completed or Interrupted.</summary>
    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    /// <summary>Whether the kiosk worked the outcome out after a restart instead of watching it.</summary>
    public bool Recovered { get; set; }

    public DateTime UpdatedAt { get; set; }
}
