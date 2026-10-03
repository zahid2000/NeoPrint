using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Notifications;

/// <summary>What a notification is about (plan section 8.4).</summary>
/// <remarks>
/// Every type the plan names is here so the app knows them all; each is sent
/// from the step that builds what it is about (<c>docs/DECISIONS.md</c>, T-146).
/// </remarks>
public enum NotificationType
{
    JobCompleted = 0,
    JobInterruptedRefundOffer = 1,
    KioskOutOfServiceRefundOffer = 2,
    ScheduleDue = 3,
    ScheduleAutoCancelled = 4,
    RefundCompleted = 5,
    FileShared = 6,
    ReferralRewarded = 7,
    ExportReady = 8,
    FileAutoDeleted = 9,
}

/// <summary>Where the push for a notification is.</summary>
public enum PushState
{
    /// <summary>Not tried yet, or tried and some device still to reach.</summary>
    Pending = 0,

    /// <summary>Every device the user had was reached.</summary>
    Sent = 1,

    /// <summary>The user has no device registered for push; the list and the hub carry it.</summary>
    NoDevice = 2,
}

/// <summary>
/// Something the user is told, in the app's list and as a push (plan section 5,
/// <c>notifications</c>; section 8.2, "notifications").
/// </summary>
/// <remarks>
/// <para>
/// The title and body are stored as message keys with their arguments
/// (<see cref="UserMessage.Encode"/>), so each reader sees them in their own
/// language - the list in the app's, the push in the device's
/// (<c>docs/DECISIONS.md</c>, T-140 and T-146).
/// </para>
/// <para>
/// <see cref="DedupKey"/> is unique: the same event told twice - a redelivered
/// message, a job that ran again - makes one notification and one push.
/// </para>
/// </remarks>
public sealed class Notification : Entity
{
    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    /// <summary>The title as <see cref="UserMessage.Encode"/> stores it.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>The body as <see cref="UserMessage.Encode"/> stores it.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>The app screen it opens: a <c>neoprint://</c> link, or null for the list itself.</summary>
    public string? Link { get; private set; }

    /// <summary>One notification per event: <c>{type}:{what}</c>, for example <c>ScheduleDue:{id}:{time}</c>.</summary>
    public string DedupKey { get; private set; } = string.Empty;

    public DateTime? ReadAt { get; private set; }

    public PushState Push { get; private set; }

    /// <summary>The devices already reached, as token hashes, so a retry does not push to them again.</summary>
    public List<string> PushedTo { get; private set; } = [];

    public bool IsRead => ReadAt is not null;

    public static Notification Create(
        Guid userId,
        NotificationType type,
        UserMessage title,
        UserMessage body,
        string? link,
        string dedupKey,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A notification belongs to a user.", nameof(userId));
        }

        if (link is not null && !link.StartsWith("neoprint://", StringComparison.Ordinal))
        {
            throw new ArgumentException("A notification opens an app screen: a neoprint:// link.", nameof(link));
        }

        return new Notification
        {
            UserId = userId,
            Type = type,
            Title = title.Encode(),
            Body = body.Encode(),
            Link = link,
            DedupKey = dedupKey,
            CreatedAt = now,
        };
    }

    /// <summary>Opened in the app. A repeat keeps the first time.</summary>
    public void MarkRead(DateTime now) => ReadAt ??= now;

    /// <summary>Whether the device behind this token hash was already reached.</summary>
    public bool WasPushedTo(string tokenHash) => PushedTo.Contains(tokenHash, StringComparer.Ordinal);

    /// <summary>One device reached. A repeat is a no-op.</summary>
    public void NotePushedTo(string tokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (!WasPushedTo(tokenHash))
        {
            PushedTo.Add(tokenHash);
        }
    }

    /// <summary>The push is done: every device reached, or there was none.</summary>
    public void FinishPush(bool anyDevice) => Push = anyDevice ? PushState.Sent : PushState.NoDevice;
}
