using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Records an administrative action (plan sections 8A.1, 10).
/// </summary>
/// <remarks>
/// Every admin operation goes through here. Secrets, tokens, file contents and
/// personal data beyond an email address are never written (plan section 2.13).
/// </remarks>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <param name="AdminUserId">Who did it.</param>
/// <param name="Action">Dotted action name, for example <c>devices.activate</c>.</param>
/// <param name="EntityType">The type of thing acted on.</param>
/// <param name="EntityId">Which one.</param>
/// <param name="Before">JSON snapshot before, where a diff is useful.</param>
/// <param name="After">JSON snapshot after.</param>
/// <param name="Ip">Caller address.</param>
/// <param name="Reason">
/// Mandatory for refunds, wallet adjustments and incident resolution
/// (plan section 8A.1).
/// </param>
/// <param name="ActorType">
/// Which kind of actor did it. Defaults to <see cref="ActorType.Admin"/>,
/// which is what almost every audited action is; a background job or a kiosk
/// says so explicitly (<c>docs/STATE_MACHINES.md</c>).
/// </param>
/// <remarks>
/// The correlation id is not a parameter: the writer reads it from the ambient
/// context, so no caller can forget to pass it along
/// (<c>docs/OBSERVABILITY.md</c>).
/// </remarks>
public sealed record AuditEntry(
    Guid? AdminUserId,
    string Action,
    string EntityType,
    string? EntityId,
    string? Before,
    string? After,
    string? Ip,
    string? Reason,
    ActorType ActorType = ActorType.Admin);
