using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Audit;

/// <summary>
/// An administrative action (plan sections 5, 8A.1, <c>audit_logs</c>).
/// </summary>
/// <remarks>
/// Every admin operation is recorded. The before and after snapshots are stored
/// as JSON so the panel can show a diff. Secrets, tokens and file contents are
/// never written here (plan section 2.13).
/// </remarks>
public sealed class AuditLog : Entity
{
    public Guid? AdminUserId { get; set; }

    /// <summary>
    /// Which kind of actor did it. An administrator for most entries, but the
    /// same actions can come from a background job or a kiosk, and an audit
    /// trail has to say which (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    public ActorType ActorType { get; set; } = ActorType.Admin;

    /// <summary>
    /// Ties the entry to everything else the same request or job produced
    /// (<c>docs/OBSERVABILITY.md</c>).
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>Dotted action name, for example <c>devices.activate</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }

    public string? Ip { get; set; }

    /// <summary>Free-text reason, mandatory for refunds, wallet adjustments and incident resolution.</summary>
    public string? Reason { get; set; }
}
