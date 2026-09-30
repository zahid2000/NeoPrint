using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Support;

/// <summary>Where the customer wrote from.</summary>
public enum ClaimChannel
{
    /// <summary>The refund status page or the hotline, with the job code from the receipt.</summary>
    Anonymous = 0,

    /// <summary>"Problem barədə bildir" in the mobile app.</summary>
    App = 1,
}

public enum ClaimStatus
{
    Open = 0,
    Answered = 1,
    Closed = 2,
}

/// <summary>
/// A customer saying something went wrong with a print (plan section 2.5,
/// "Müştəri müraciəti"). Part of the flow, not an extra: when the kiosk went
/// quiet there is no automatic refund, and this is how the customer is heard.
/// The claim is attached to the job's investigation card in the admin panel.
/// </summary>
public sealed class SupportClaim : Entity
{
    public const int MaxTextLength = 1000;

    public Guid? JobId { get; set; }

    public string? JobCode { get; set; }

    public Guid? KioskId { get; set; }

    public Guid? UserId { get; set; }

    public ClaimChannel Channel { get; set; }

    /// <summary>What the customer wrote; shown to admins only.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>An email or phone an anonymous customer left for the answer, if they chose to.</summary>
    public string? Contact { get; set; }

    public ClaimStatus Status { get; private set; } = ClaimStatus.Open;

    public string? Reply { get; private set; }

    public Guid? RepliedBy { get; private set; }

    public DateTime? RepliedAt { get; private set; }

    public static SupportClaim Open(
        ClaimChannel channel, Guid? jobId, string? jobCode, Guid? kioskId, Guid? userId, string text, string? contact, DateTime now)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > MaxTextLength)
        {
            throw new DomainException(UserMessage.Of("support.textLength", ("max", MaxTextLength)));
        }

        return new SupportClaim
        {
            Channel = channel,
            JobId = jobId,
            JobCode = jobCode,
            KioskId = kioskId,
            UserId = userId,
            Text = trimmed,
            Contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
            CreatedAt = now,
        };
    }

    public void Answer(Guid adminId, string reply, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reply);

        Reply = reply.Trim();
        RepliedBy = adminId;
        RepliedAt = now;
        Status = ClaimStatus.Answered;
    }

    public void Close() => Status = ClaimStatus.Closed;
}
