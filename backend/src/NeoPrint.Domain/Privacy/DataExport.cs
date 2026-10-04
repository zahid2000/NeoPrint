using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Privacy;

/// <summary>Where a "Məlumatlarımı yüklə" archive is (plan section 2.13).</summary>
public enum DataExportStatus
{
    /// <summary>Asked for; the archive is being made.</summary>
    Preparing = 0,

    /// <summary>Made; downloadable until <see cref="DataExport.ExpiresAt"/>.</summary>
    Ready = 1,

    /// <summary>Could not be made; <see cref="DataExport.FailureReason"/> says why. The user asks again.</summary>
    Failed = 2,

    /// <summary>Past its 24 hours; the archive is deleted.</summary>
    Expired = 3,
}

/// <summary>
/// One "Məlumatlarımı yüklə" archive: the profile, payments, receipts and files
/// as a ZIP, made in the background and kept for 24 hours (plan section 2.13;
/// <c>docs/DECISIONS.md</c> T-147).
/// </summary>
public sealed class DataExport : Entity
{
    /// <summary>How long a ready archive can be downloaded.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid UserId { get; private set; }

    public DataExportStatus Status { get; private set; }

    /// <summary>The ZIP, encrypted in the temp bucket; null until ready and after it expires.</summary>
    public FileObject? Archive { get; private set; }

    public long SizeBytes { get; private set; }

    /// <summary>False when the files were over <c>Privacy.ExportMaxMegabytes</c>: listed, not included.</summary>
    public bool IncludesFiles { get; private set; }

    public DateTime? ReadyAt { get; private set; }

    public DateTime? ExpiresAt { get; private set; }

    /// <summary>Why it failed, as <see cref="UserMessage.Encode"/> stores it.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The transitions it may make.</summary>
    public static readonly StateMachine<DataExportStatus> Transitions =
        new StateMachine<DataExportStatus>(nameof(DataExport))
            .Allow(DataExportStatus.Preparing, DataExportStatus.Ready, DataExportStatus.Failed)
            .Allow(DataExportStatus.Ready, DataExportStatus.Expired);

    public static DataExport Request(Guid userId, DateTime now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("An export belongs to a user.", nameof(userId));
        }

        return new DataExport { UserId = userId, Status = DataExportStatus.Preparing, CreatedAt = now };
    }

    /// <summary>Still being made, or ready and not past its time: a new request returns this one.</summary>
    public bool IsCurrent(DateTime now)
        => Status == DataExportStatus.Preparing || (Status == DataExportStatus.Ready && now < ExpiresAt);

    public void MarkReady(FileObject archive, long sizeBytes, bool includesFiles, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(archive);

        Move(DataExportStatus.Ready, now);
        Archive = archive;
        SizeBytes = sizeBytes;
        IncludesFiles = includesFiles;
        ReadyAt = now;
        ExpiresAt = now + Lifetime;
    }

    public void MarkFailed(UserMessage reason, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reason);

        Move(DataExportStatus.Failed, now);
        FailureReason = reason.Encode();
    }

    /// <summary>Past its time: the archive's object is the caller's to delete first.</summary>
    public void Expire(DateTime now)
    {
        Move(DataExportStatus.Expired, now);
        Archive = null;
    }

    /// <summary>Only the system moves an export; the user only asks for one.</summary>
    private void Move(DataExportStatus to, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, to, ActorContext.System(), now));
        Status = to;
    }
}
