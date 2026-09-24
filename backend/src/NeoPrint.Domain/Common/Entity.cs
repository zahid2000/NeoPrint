namespace NeoPrint.Domain.Common;

/// <summary>
/// Base for every persisted aggregate.
/// </summary>
/// <remarks>
/// Identifiers are GUIDs and timestamps are UTC throughout (plan section 2.1:
/// the server stores UTC, the UI renders Asia/Baku).
/// </remarks>
public abstract class Entity
{
    private List<StateTransition>? _pendingTransitions;

    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Incremented on every save, and checked while saving
    /// (<c>docs/IDEMPOTENCY.md</c>, concurrency).
    /// </summary>
    /// <remarks>
    /// Two requests that read the same document and both write it back would
    /// otherwise silently lose one of the changes. With a row version the
    /// second write is refused and the operation can be retried against what
    /// is actually stored.
    ///
    /// Named RowVersion rather than Version because several aggregates already
    /// have a Version of their own - a legal document's published version, a
    /// consent's accepted version - and those mean something else entirely.
    /// </remarks>
    public int RowVersion { get; set; }

    /// <summary>
    /// State changes made since this aggregate was loaded, waiting to be
    /// written alongside it.
    /// </summary>
    /// <remarks>
    /// Collected by the domain transition methods and drained by the
    /// repository, so recording a transition is not something a handler can
    /// forget to do (<c>docs/STATE_MACHINES.md</c>).
    /// </remarks>
    public IReadOnlyList<StateTransition> PendingTransitions
        => _pendingTransitions ?? (IReadOnlyList<StateTransition>)[];

    /// <summary>Records a transition, ignoring the no-op a repeated event produces.</summary>
    protected void Record(StateTransition? transition)
    {
        if (transition is null)
        {
            return;
        }

        _pendingTransitions ??= [];
        _pendingTransitions.Add(transition);
    }

    /// <summary>
    /// Takes the pending transitions, leaving none behind.
    /// </summary>
    /// <remarks>
    /// Called by the repository as it saves. Draining rather than copying means
    /// a second save cannot write the same transitions again.
    /// </remarks>
    public IReadOnlyList<StateTransition> DrainTransitions()
    {
        if (_pendingTransitions is null or { Count: 0 })
        {
            return [];
        }

        var drained = _pendingTransitions;
        _pendingTransitions = null;

        return drained;
    }
}
