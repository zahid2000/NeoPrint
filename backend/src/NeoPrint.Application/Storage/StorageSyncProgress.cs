namespace NeoPrint.Application.Storage;

/// <summary>
/// What the panel shows about a running sync job (plan section 2.10, the
/// live progress in the sync wizard).
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="Mode">Migration or Mirror.</param>
/// <param name="Status">Pending, Running, Paused, Verifying, Completed, Failed or Cancelled.</param>
/// <param name="TotalObjects">Objects found on the source when the job started.</param>
/// <param name="CopiedObjects">Objects copied and verified so far.</param>
/// <param name="FailedObjects">Objects that could not be copied after every retry.</param>
/// <param name="TotalBytes">Bytes on the source when the job started.</param>
/// <param name="CopiedBytes">Bytes copied so far.</param>
/// <param name="FailureReason">Why the job failed, when it did.</param>
/// <param name="At">When this snapshot was taken, so the panel can work out a speed.</param>
public sealed record StorageSyncProgress(
    Guid JobId,
    string Mode,
    string Status,
    long TotalObjects,
    long CopiedObjects,
    long FailedObjects,
    long TotalBytes,
    long CopiedBytes,
    string? FailureReason,
    DateTime At);

/// <summary>
/// A provider started or stopped answering its health check
/// (plan section 6, <c>StorageHealthChanged</c> on <c>/hubs/admin</c>).
/// </summary>
/// <param name="Status">Healthy or Unhealthy.</param>
/// <param name="Message">What is wrong, in the panel's language, or null.</param>
public sealed record StorageHealthChanged(
    Guid ProviderId, string Name, string Role, string Status, string? Message, DateTime At);
