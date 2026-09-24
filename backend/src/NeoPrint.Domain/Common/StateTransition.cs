namespace NeoPrint.Domain.Common;

/// <summary>
/// One recorded state change (<c>docs/STATE_MACHINES.md</c>,
/// <c>state_transitions</c>).
/// </summary>
/// <remarks>
/// Kept in its own collection rather than as an array on the aggregate: a kiosk
/// or a print job accumulates transitions for as long as it exists, and an
/// unbounded array inside a document is a document that eventually stops
/// fitting. The plan says the same (section 5).
/// </remarks>
public sealed class StateTransition : Entity
{
    /// <summary>The aggregate this happened to, for example <c>KioskDevice</c>.</summary>
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary>The status before, as its name.</summary>
    /// <remarks>
    /// Stored as a string rather than a number so the record still reads
    /// correctly after an enum gains a member.
    /// </remarks>
    public string FromStatus { get; set; } = string.Empty;

    public string ToStatus { get; set; } = string.Empty;

    public ActorType ActorType { get; set; } = ActorType.System;

    public Guid? ActorId { get; set; }

    public Guid? DeviceId { get; set; }

    public string? Reason { get; set; }

    public string? CorrelationId { get; set; }

    internal static StateTransition For<TStatus>(
        string entityType, Guid entityId, TStatus from, TStatus to, ActorContext actor, DateTime at)
        where TStatus : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new StateTransition
        {
            EntityType = entityType,
            EntityId = entityId,
            FromStatus = from.ToString(),
            ToStatus = to.ToString(),
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            DeviceId = actor.DeviceId,
            Reason = actor.Reason,
            CorrelationId = actor.CorrelationId,
            CreatedAt = at,
        };
    }
}
