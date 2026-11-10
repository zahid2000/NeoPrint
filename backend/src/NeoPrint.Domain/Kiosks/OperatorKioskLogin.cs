using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// An operator at the kiosk (T-392; <c>docs/DECISIONS.md</c> 2026-11-10): the
/// kiosk shows a one-off QR, the operator reads it with the operator panel,
/// and the kiosk's admin menu opens in that operator's name. Every change made
/// there goes to the backend under the operator's account and into the audit
/// log; the kiosk holds no operator secret, only this login's id.
/// </summary>
public sealed class OperatorKioskLogin : Entity
{
    /// <summary>How long the QR on the kiosk's screen can be read.</summary>
    public static readonly TimeSpan QrFor = TimeSpan.FromMinutes(2);

    /// <summary>How long the menu stays open in the operator's name once they confirmed.</summary>
    public static readonly TimeSpan OpenFor = TimeSpan.FromMinutes(15);

    public Guid KioskId { get; set; }

    /// <summary>The random part of the QR: 128 bits, used once.</summary>
    public string Code { get; set; } = string.Empty;

    public OperatorKioskLoginStatus Status { get; private set; } = OperatorKioskLoginStatus.Pending;

    /// <summary>Until when the QR can be read (Pending), then until when the menu is open (Approved).</summary>
    public DateTime ExpiresAt { get; set; }

    public Guid? OperatorId { get; private set; }

    /// <summary>What the kiosk's menu says: whose name it is open in.</summary>
    public string? OperatorName { get; private set; }

    public DateTime? ApprovedAt { get; private set; }

    public DateTime? EndedAt { get; private set; }

    public static OperatorKioskLogin Start(Guid kioskId, string code, DateTime utcNow) => new()
    {
        KioskId = kioskId,
        Code = code,
        CreatedAt = utcNow,
        ExpiresAt = utcNow + QrFor,
    };

    /// <summary>Whether the menu may act in the operator's name now.</summary>
    public bool IsOpen(DateTime utcNow) => Status == OperatorKioskLoginStatus.Approved && utcNow < ExpiresAt;

    /// <summary>The operator read the QR and confirmed: the menu opens in their name.</summary>
    /// <exception cref="DomainException">The QR was read already, or its time is up.</exception>
    public void Approve(Guid operatorId, string operatorName, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        if (Status != OperatorKioskLoginStatus.Pending || utcNow >= ExpiresAt)
        {
            throw new DomainException("operatorLogin.expired");
        }

        Status = OperatorKioskLoginStatus.Approved;
        OperatorId = operatorId;
        OperatorName = operatorName.Trim();
        ApprovedAt = utcNow;
        ExpiresAt = utcNow + OpenFor;
    }

    /// <summary>The menu was closed: nothing more in the operator's name. Twice is the same end.</summary>
    public void End(DateTime utcNow)
    {
        if (Status is OperatorKioskLoginStatus.Ended)
        {
            return;
        }

        Status = OperatorKioskLoginStatus.Ended;
        EndedAt = utcNow;
    }
}

public enum OperatorKioskLoginStatus
{
    Pending = 0,
    Approved = 1,
    Ended = 2,
}
