using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Storage;

/// <summary>What a sync job does (plan section 2.10).</summary>
public enum StorageSyncMode
{
    /// <summary>
    /// One-off: copy everything from the source to the target, keep new
    /// writes going to both while it runs, then optionally make the target
    /// primary.
    /// </summary>
    Migration = 0,

    /// <summary>Continuous: every write and delete on the primary is repeated on the secondary.</summary>
    Mirror = 1,
}

/// <summary>Where a sync job stands.</summary>
public enum StorageSyncStatus
{
    Pending = 0,
    Running = 1,
    Paused = 2,

    /// <summary>Everything is copied; checking the target has every object before switching.</summary>
    Verifying = 3,

    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>Where the copy has got to, so a restart resumes rather than starts over.</summary>
/// <param name="Bucket">The bucket being copied, by role: Files, Receipts or Temp.</param>
/// <param name="LastKey">
/// The last key copied in that bucket. S3 lists keys in lexicographic order,
/// so resuming with <c>StartAfter</c> this key skips exactly what is done.
/// </param>
public sealed record StorageSyncCheckpoint(string Bucket, string? LastKey);

/// <summary>
/// One run of copying objects between two providers (plan section 2.10,
/// <c>storage_sync_jobs</c>).
/// </summary>
/// <remarks>
/// <para>
/// Progress is recorded as it goes - the checkpoint and the counters - so a
/// worker that dies, is redeployed or loses its machine leaves a job another
/// worker can pick up from where it stopped. The lease is what decides which
/// worker that is: a job whose lease has lapsed is free to take.
/// </para>
/// <para>
/// Status changes go through the transition methods, as everywhere else
/// (<c>docs/STATE_MACHINES.md</c>). Counters and the checkpoint are progress,
/// not state, and are written directly.
/// </para>
/// </remarks>
public sealed class StorageSyncJob : Entity
{
    public StorageSyncMode Mode { get; set; }

    public Guid SourceId { get; set; }

    public Guid TargetId { get; set; }

    public StorageSyncStatus Status { get; private set; } = StorageSyncStatus.Pending;

    public long TotalObjects { get; set; }

    public long CopiedObjects { get; set; }

    public long FailedObjects { get; set; }

    public long TotalBytes { get; set; }

    public long CopiedBytes { get; set; }

    public StorageSyncCheckpoint? Checkpoint { get; set; }

    /// <summary>Make the target primary once everything is copied and verified.</summary>
    public bool SwitchPrimaryOnComplete { get; set; }

    public Guid? StartedBy { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>Why the job failed, for the panel.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The worker holding the job, so two cannot run it at once.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>When the lease lapses unless renewed. A lapsed lease means the worker is gone.</summary>
    public DateTime? LeaseExpiresAt { get; set; }

    public static readonly StateMachine<StorageSyncStatus> Transitions =
        new StateMachine<StorageSyncStatus>(nameof(StorageSyncJob))
            .Allow(StorageSyncStatus.Pending, StorageSyncStatus.Running, StorageSyncStatus.Cancelled)
            .Allow(
                StorageSyncStatus.Running,
                StorageSyncStatus.Paused,
                StorageSyncStatus.Verifying,
                StorageSyncStatus.Failed,
                StorageSyncStatus.Cancelled)
            .Allow(StorageSyncStatus.Paused, StorageSyncStatus.Running, StorageSyncStatus.Cancelled)
            .Allow(StorageSyncStatus.Verifying, StorageSyncStatus.Completed, StorageSyncStatus.Failed)
            // "Retry the failures" re-opens a finished job rather than starting
            // a second one, so its history stays in one place.
            .Allow(StorageSyncStatus.Failed, StorageSyncStatus.Running, StorageSyncStatus.Verifying)
            .Allow(StorageSyncStatus.Completed, StorageSyncStatus.Running);

    /// <summary>Whether the job still has work to do or could be resumed.</summary>
    public bool IsActive => Status is StorageSyncStatus.Pending
        or StorageSyncStatus.Running
        or StorageSyncStatus.Paused
        or StorageSyncStatus.Verifying;

    public void Start(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Running, actor, now));
        Status = StorageSyncStatus.Running;
        StartedAt ??= now;
        FailureReason = null;
    }

    public void Pause(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Paused, actor, now));
        Status = StorageSyncStatus.Paused;
    }

    public void BeginVerification(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Verifying, actor, now));
        Status = StorageSyncStatus.Verifying;
    }

    /// <summary>
    /// Sends a failed job back to verification, which copies whatever is still
    /// missing on the target - the failed objects, exactly - and switches only
    /// once all of it is there. "Retry the failures" in the panel.
    /// </summary>
    public void RetryVerification(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Verifying, actor, now));
        Status = StorageSyncStatus.Verifying;
        FailureReason = null;
        FinishedAt = null;
    }

    public void Complete(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Completed, actor, now));
        Status = StorageSyncStatus.Completed;
        FinishedAt = now;
        ReleaseLease();
    }

    public void Fail(string reason, ActorContext actor, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Failed, actor, now));
        Status = StorageSyncStatus.Failed;
        FailureReason = reason;
        FinishedAt = now;
        ReleaseLease();
    }

    public void Cancel(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, StorageSyncStatus.Cancelled, actor, now));
        Status = StorageSyncStatus.Cancelled;
        FinishedAt = now;
        ReleaseLease();
    }

    public void ReleaseLease()
    {
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    // -----------------------------------------------------------------------
    // Emptying the source after a migration (plan section 2.10)
    // -----------------------------------------------------------------------

    /// <summary>When an administrator confirmed the source could be emptied.</summary>
    public DateTime? SourcePurgeRequestedAt { get; private set; }

    public Guid? SourcePurgeRequestedBy { get; private set; }

    /// <summary>When the last object that could go was deleted from the source.</summary>
    public DateTime? SourcePurgedAt { get; private set; }

    /// <summary>Objects deleted from the source so far.</summary>
    public long SourcePurgedObjects { get; set; }

    /// <summary>Objects kept on the source because they were still needed there.</summary>
    public long SourceKeptObjects { get; set; }

    /// <summary>
    /// The earliest moment the source may be emptied: the job's completion
    /// plus <c>Storage.SourceRetentionDays</c>. Null while the job is not a
    /// completed migration.
    /// </summary>
    public DateTime? SourcePurgeAllowedFrom(int retentionDays)
        => Mode == StorageSyncMode.Migration && Status == StorageSyncStatus.Completed && FinishedAt is { } finished
            ? finished.AddDays(Math.Max(0, retentionDays))
            : null;

    /// <summary>
    /// Records the administrator's go-ahead to delete the migrated objects
    /// from the source (plan section 2.10: after
    /// <c>Storage.SourceRetentionDays</c>, with the administrator's approval).
    /// </summary>
    /// <remarks>
    /// Asking again while a purge is under way, or after it finished, changes
    /// nothing: the request is the same request.
    /// </remarks>
    /// <exception cref="DomainException">
    /// Not a completed migration, or the retention period has not passed.
    /// </exception>
    public void RequestSourcePurge(int retentionDays, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var allowedFrom = SourcePurgeAllowedFrom(retentionDays)
            ?? throw new DomainException("storage.purgeAfterMigration");

        if (now < allowedFrom)
        {
            throw new DomainException(UserMessage.Of(
                "storage.purgeTooEarly",
                ("days", retentionDays),
                ("from", allowedFrom.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture))));
        }

        if (SourcePurgeRequestedAt is not null)
        {
            return;
        }

        SourcePurgeRequestedAt = now;
        SourcePurgeRequestedBy = actor.ActorId;
    }

    /// <summary>Marks the purge as finished.</summary>
    public void CompleteSourcePurge(DateTime now)
    {
        if (SourcePurgeRequestedAt is null)
        {
            throw new DomainException("storage.purgeNotRequested");
        }

        SourcePurgedAt ??= now;
    }
}

/// <summary>One object a sync could not copy (<c>storage_sync_failures</c>).</summary>
public sealed class StorageSyncFailure : Entity
{
    public Guid JobId { get; set; }

    /// <summary>The bucket role: Files, Receipts or Temp.</summary>
    public string Bucket { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>What went wrong, for the panel. Never a credential.</summary>
    public string Error { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTime LastAttemptAt { get; set; }

    /// <summary>Set once a later retry copied it.</summary>
    public DateTime? ResolvedAt { get; set; }
}
