namespace NeoPrint.Domain.Common;

/// <summary>
/// Who caused something to happen (<c>docs/STATE_MACHINES.md</c>,
/// <c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// Recorded on audit entries and on state transitions. An id alone is not
/// enough: the same operation can be triggered by a customer, by an operator on
/// the shop floor, by the kiosk itself, or by a background job, and telling
/// those apart is the difference between an audit trail and a list of rows.
/// </remarks>
public enum ActorType
{
    /// <summary>No identified actor - a background job, a timer, a consumer.</summary>
    System = 0,

    /// <summary>A registered mobile user.</summary>
    User = 1,

    /// <summary>An administrator or operator acting through a panel.</summary>
    Admin = 2,

    /// <summary>A kiosk device acting on its own behalf.</summary>
    Kiosk = 3,

    /// <summary>Somebody using a kiosk without signing in.</summary>
    AnonymousKioskSession = 4,
}
