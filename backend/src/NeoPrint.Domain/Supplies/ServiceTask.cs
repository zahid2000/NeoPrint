using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Supplies;

public enum ServiceTaskType
{
    RefillPaper = 0,
    ReplaceToner = 1,
    ReplaceDrum = 2,
    PrinterRepair = 3,
    Cleaning = 4,
    Other = 5,
}

public enum ServiceTaskStatus
{
    /// <summary>Nobody's yet: in the common pool.</summary>
    Open = 0,

    /// <summary>Somebody's, not started.</summary>
    Assigned = 1,

    InProgress = 2,
    Done = 3,
    Cancelled = 4,
}

/// <summary>What asked for the task.</summary>
public enum ServiceTaskSource
{
    AlertRule = 0,
    Forecast = 1,
    Incident = 2,
    Manual = 3,
}

/// <summary>What was done, as the operator recorded it on finishing (plan section 2.15).</summary>
public sealed class ServiceTaskCompletion
{
    /// <summary>"A4" or "A3", for a paper refill.</summary>
    public string? Paper { get; set; }

    /// <summary>The tray's count after the refill minus before it.</summary>
    public int? SheetsAdded { get; set; }

    /// <summary>What was taken from the warehouse for it.</summary>
    public Guid? InventoryItemId { get; set; }

    public decimal? Quantity { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Something an operator has to do at a kiosk (plan section 2.15,
/// <c>service_tasks</c>): fill a tray, change a toner, look at a printer.
/// </summary>
/// <remarks>
/// <para>
/// Open (the pool) → Assigned → InProgress → Done, and Cancelled from anywhere
/// before Done (<c>docs/STATE_MACHINES.md</c>). An assigned task can be handed
/// to somebody else or put back in the pool; one that is in progress is its
/// operator's until it ends.
/// </para>
/// <para>
/// A task the system opened carries a <see cref="Key"/>, unique among the
/// tasks still to be done: a tray that stays low is one task, however many
/// times its level is looked at.
/// </para>
/// </remarks>
public sealed class ServiceTask : Entity
{
    public static readonly StateMachine<ServiceTaskStatus> Transitions =
        new StateMachine<ServiceTaskStatus>(nameof(ServiceTask))
            .Allow(ServiceTaskStatus.Open, ServiceTaskStatus.Assigned, ServiceTaskStatus.InProgress, ServiceTaskStatus.Cancelled)
            .Allow(ServiceTaskStatus.Assigned, ServiceTaskStatus.Open, ServiceTaskStatus.InProgress, ServiceTaskStatus.Cancelled)
            .Allow(ServiceTaskStatus.InProgress, ServiceTaskStatus.Done, ServiceTaskStatus.Cancelled);

    public Guid KioskId { get; set; }

    public ServiceTaskType Type { get; set; }

    public TaskPriority Priority { get; private set; } = TaskPriority.Normal;

    public ServiceTaskStatus Status { get; private set; } = ServiceTaskStatus.Open;

    /// <summary>Still to be done. Stored for the unique index on <see cref="Key"/>.</summary>
    public bool Active { get; private set; } = true;

    public ServiceTaskSource Source { get; set; }

    /// <summary>The supply it is about, when it is about one.</summary>
    public SupplyResource? Resource { get; set; }

    public string? Variant { get; set; }

    /// <summary>The administrator it is with; null while it is in the pool.</summary>
    public Guid? AssignedTo { get; private set; }

    public DateTime? AssignedAt { get; private set; }

    public DateTime? DueAt { get; set; }

    /// <summary>A stored message, or what an administrator wrote.</summary>
    public string? Notes { get; set; }

    /// <summary>The administrator who opened it by hand; null when the system did.</summary>
    public Guid? CreatedBy { get; set; }

    public ServiceTaskCompletion? Completion { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    /// <summary>Since when it has been Critical: what the wait before an escalation is counted from.</summary>
    public DateTime? CriticalSince { get; private set; }

    /// <summary>When the administrators were told it had waited too long; once.</summary>
    public DateTime? EscalatedAt { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>
    /// <c>{kiosk}:{type}</c> or <c>{kiosk}:{type}:{variant}</c> for a task the
    /// system opened; null for one opened by hand - an administrator may open
    /// as many as they like.
    /// </summary>
    public string? Key { get; set; }

    public static string KeyOf(Guid kioskId, ServiceTaskType type, string? variant)
        => string.IsNullOrWhiteSpace(variant)
            ? $"{kioskId:N}:{type}"
            : $"{kioskId:N}:{type}:{variant.Trim().ToLowerInvariant()}";

    /// <summary>The task a supply that is running low asks for.</summary>
    public static ServiceTaskType TypeFor(SupplyResource resource)
        => resource switch
        {
            SupplyResource.PaperA4 or SupplyResource.PaperA3 => ServiceTaskType.RefillPaper,
            SupplyResource.Toner => ServiceTaskType.ReplaceToner,
            SupplyResource.Drum => ServiceTaskType.ReplaceDrum,
            _ => ServiceTaskType.Other,
        };

    public static ServiceTask Open(
        Guid kioskId,
        ServiceTaskType type,
        TaskPriority priority,
        ServiceTaskSource source,
        DateTime now,
        SupplyResource? resource = null,
        string? variant = null,
        string? notes = null,
        DateTime? dueAt = null,
        Guid? createdBy = null)
        => new()
        {
            KioskId = kioskId,
            Type = type,
            Priority = priority,
            CriticalSince = priority == TaskPriority.Critical ? now : null,
            Source = source,
            Resource = resource,
            Variant = string.IsNullOrWhiteSpace(variant) ? null : variant.Trim(),
            Notes = notes,
            DueAt = dueAt,
            CreatedBy = createdBy,
            CreatedAt = now,

            // A paper size is part of what makes a refill one of a kind: A4 and A3 are two trays.
            Key = source == ServiceTaskSource.Manual
                ? null
                : KeyOf(kioskId, type, type == ServiceTaskType.RefillPaper ? resource?.ToString() : variant),
        };

    /// <summary>
    /// Gives the task to an administrator, or back to the pool.
    /// </summary>
    /// <param name="adminId">Null: nobody's again.</param>
    /// <exception cref="DomainException">The task is being worked on, or is over.</exception>
    public void Assign(Guid? adminId, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status is not (ServiceTaskStatus.Open or ServiceTaskStatus.Assigned))
        {
            throw new DomainException("serviceTasks.notAssignable");
        }

        if (adminId == AssignedTo)
        {
            return;
        }

        var next = adminId is null ? ServiceTaskStatus.Open : ServiceTaskStatus.Assigned;

        if (next != Status)
        {
            Record(Transitions.Transition(Id, Status, next, actor, now));
        }

        Status = next;
        AssignedTo = adminId;
        AssignedAt = adminId is null ? null : now;
    }

    /// <summary>
    /// "Başla": the operator is at it. One taken straight from the pool
    /// becomes theirs.
    /// </summary>
    /// <exception cref="DomainException">It is somebody else's, already started, or over.</exception>
    public void Start(Guid adminId, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status is not (ServiceTaskStatus.Open or ServiceTaskStatus.Assigned))
        {
            throw new DomainException("serviceTasks.notStartable");
        }

        if (AssignedTo is { } holder && holder != adminId)
        {
            throw new DomainException("serviceTasks.notYours");
        }

        Record(Transitions.Transition(Id, Status, ServiceTaskStatus.InProgress, actor, now));

        Status = ServiceTaskStatus.InProgress;
        AssignedTo = adminId;
        AssignedAt ??= now;
        StartedAt = now;
    }

    /// <exception cref="DomainException">It was not started.</exception>
    public void Complete(ServiceTaskCompletion completion, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(actor);

        if (Status != ServiceTaskStatus.InProgress)
        {
            throw new DomainException("serviceTasks.notInProgress");
        }

        Record(Transitions.Transition(Id, Status, ServiceTaskStatus.Done, actor, now));

        Status = ServiceTaskStatus.Done;
        Active = false;
        Completion = completion;
        CompletedAt = now;
    }

    /// <summary>Not to be done after all. Repeating it changes nothing.</summary>
    /// <exception cref="DomainException">It is already done.</exception>
    public void Cancel(string reason, ActorContext actor, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == ServiceTaskStatus.Cancelled)
        {
            return;
        }

        if (Status == ServiceTaskStatus.Done)
        {
            throw new DomainException("serviceTasks.alreadyDone");
        }

        Record(Transitions.Transition(Id, Status, ServiceTaskStatus.Cancelled, actor, now));

        Status = ServiceTaskStatus.Cancelled;
        Active = false;
        CancelReason = reason;
        CompletedAt = now;
    }

    /// <summary>
    /// An administrator rewrites what the task says: how urgent it is, by
    /// when, and the note. Who has it and how far it is are not touched here.
    /// </summary>
    /// <exception cref="DomainException">The task is over.</exception>
    public void Edit(TaskPriority priority, string? notes, DateTime? dueAt, DateTime now)
    {
        if (!Active)
        {
            throw new DomainException("serviceTasks.notEditable");
        }

        if (priority != Priority)
        {
            // Critical from now, or no longer: the escalation counts from this moment and may speak again.
            CriticalSince = priority == TaskPriority.Critical ? now : null;
            EscalatedAt = null;
            Priority = priority;
        }

        Notes = notes;
        DueAt = dueAt;
    }

    /// <summary>A task only becomes more urgent: an alert going Critical raises it, one easing does not lower it.</summary>
    /// <returns>True when the priority rose.</returns>
    public bool RaisePriority(TaskPriority priority, DateTime now)
    {
        if (!Active || priority <= Priority)
        {
            return false;
        }

        Priority = priority;

        if (priority == TaskPriority.Critical)
        {
            CriticalSince = now;
        }

        return true;
    }

    /// <summary>
    /// Whether a Critical task has waited longer than allowed without being
    /// started, and nobody has been told yet (plan section 2.15).
    /// </summary>
    public bool NeedsEscalation(DateTime now, TimeSpan after)
        => Priority == TaskPriority.Critical
           && Status is ServiceTaskStatus.Open or ServiceTaskStatus.Assigned
           && EscalatedAt is null
           && CriticalSince is { } since
           && now - since > after;

    public void MarkEscalated(DateTime now) => EscalatedAt ??= now;
}
