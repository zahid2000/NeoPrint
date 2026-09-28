namespace NeoPrint.Domain.Common;

/// <summary>
/// The transitions a status is allowed to make
/// (<c>docs/STATE_MACHINES.md</c>).
/// </summary>
/// <remarks>
/// One table per aggregate, declared next to the aggregate itself, so the rules
/// are read in one place instead of being spread across the handlers that
/// happen to call them. Anything not in the table is refused: the document says
/// an unauthorised transition must not be possible, and a table that lists what
/// is allowed fails closed as new statuses are added.
/// </remarks>
/// <typeparam name="TStatus">The status enum this machine governs.</typeparam>
public sealed class StateMachine<TStatus>
    where TStatus : struct, Enum
{
    private readonly string _entityType;
    private readonly Dictionary<TStatus, HashSet<TStatus>> _allowed = [];

    /// <param name="entityType">
    /// The aggregate's name, as recorded on each transition.
    /// </param>
    public StateMachine(string entityType) => _entityType = entityType;

    /// <summary>Declares that <paramref name="from"/> may become any of <paramref name="to"/>.</summary>
    public StateMachine<TStatus> Allow(TStatus from, params TStatus[] to)
    {
        ArgumentNullException.ThrowIfNull(to);

        if (!_allowed.TryGetValue(from, out var targets))
        {
            targets = [];
            _allowed[from] = targets;
        }

        foreach (var target in to)
        {
            targets.Add(target);
        }

        return this;
    }

    /// <summary>Declares that every status may become <paramref name="to"/>.</summary>
    /// <remarks>
    /// For the sinks the state machines describe with <c>* → Disabled</c>. The
    /// status itself is excluded, so this does not quietly make every
    /// transition a self-transition.
    /// </remarks>
    public StateMachine<TStatus> AllowFromAny(TStatus to)
    {
        foreach (var from in Enum.GetValues<TStatus>())
        {
            if (!Equals(from, to))
            {
                Allow(from, to);
            }
        }

        return this;
    }

    public bool IsAllowed(TStatus from, TStatus to)
        => _allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Applies a transition, or explains why it cannot happen.
    /// </summary>
    /// <returns>
    /// The transition to record, or <c>null</c> when the status is already the
    /// requested one. Re-applying a transition is a no-op rather than an error:
    /// the same event can arrive twice, and the document requires that to be
    /// safe.
    /// </returns>
    /// <exception cref="DomainException">The transition is not allowed.</exception>
    public StateTransition? Transition(
        Guid entityId, TStatus from, TStatus to, ActorContext actor, DateTime at)
    {
        if (Equals(from, to))
        {
            return null;
        }

        if (!IsAllowed(from, to))
        {
            throw new IllegalStateTransitionException(_entityType, from.ToString(), to.ToString());
        }

        return StateTransition.For(_entityType, entityId, from, to, actor, at);
    }
}

/// <summary>A status change the state machine does not allow.</summary>
/// <remarks>
/// A <see cref="DomainException"/>, so it surfaces as a 409 with a message
/// written to be read rather than as an unexplained failure.
/// </remarks>
public sealed class IllegalStateTransitionException : DomainException
{
    public IllegalStateTransitionException(string entityType, string from, string to)
        : base(UserMessage.Of("common.illegalTransition", ("from", from), ("to", to)))
    {
        EntityType = entityType;
        From = from;
        To = to;
    }

    public string EntityType { get; }

    public string From { get; }

    public string To { get; }

    /// <summary>For the log: which entity, from where, to where. The caller reads <see cref="DomainException.Text"/>.</summary>
    public override string Message => $"{EntityType} cannot go from {From} to {To}.";
}
