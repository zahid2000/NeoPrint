namespace NeoPrint.Domain.Common;

/// <summary>
/// Who is doing something, and why (<c>docs/STATE_MACHINES.md</c>).
/// </summary>
/// <remarks>
/// Every state transition records one. A status change with no actor is a row
/// that cannot be explained afterwards, which is exactly what an audit trail
/// exists to prevent - so the transition methods take this rather than leaving
/// it to the caller to remember.
/// </remarks>
/// <param name="ActorType">Which kind of actor.</param>
/// <param name="ActorId">
/// The user, administrator or kiosk id, where there is one. A background job
/// has none.
/// </param>
/// <param name="DeviceId">The device involved, for anything a kiosk did.</param>
/// <param name="Reason">
/// Why. Mandatory for the destructive actions plan section 8A.1 lists;
/// optional elsewhere, but worth filling in.
/// </param>
/// <param name="CorrelationId">
/// Ties the transition to everything else the same request or job produced
/// (<c>docs/OBSERVABILITY.md</c>).
/// </param>
public sealed record ActorContext(
    ActorType ActorType,
    Guid? ActorId = null,
    Guid? DeviceId = null,
    string? Reason = null,
    string? CorrelationId = null)
{
    /// <summary>A background job, a timer or a consumer acting on its own.</summary>
    public static ActorContext System(string? reason = null, string? correlationId = null)
        => new(ActorType.System, Reason: reason, CorrelationId: correlationId);

    /// <summary>An administrator or operator acting through a panel.</summary>
    public static ActorContext Admin(
        Guid adminUserId, string? reason = null, string? correlationId = null)
        => new(ActorType.Admin, adminUserId, Reason: reason, CorrelationId: correlationId);

    /// <summary>A kiosk device acting on its own behalf.</summary>
    public static ActorContext Kiosk(
        Guid deviceId, string? reason = null, string? correlationId = null)
        => new(ActorType.Kiosk, DeviceId: deviceId, Reason: reason, CorrelationId: correlationId);

    /// <summary>A registered mobile user.</summary>
    public static ActorContext User(
        Guid userId, string? reason = null, string? correlationId = null)
        => new(ActorType.User, userId, Reason: reason, CorrelationId: correlationId);

    /// <summary>The same actor, with a reason attached.</summary>
    public ActorContext Because(string? reason) => this with { Reason = reason };

    /// <summary>The same actor, carrying a correlation id.</summary>
    public ActorContext WithCorrelationId(string? correlationId)
        => this with { CorrelationId = correlationId };
}
