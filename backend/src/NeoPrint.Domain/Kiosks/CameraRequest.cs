using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>Where a request for a kiosk's recorded minutes stands (T-483).</summary>
public enum CameraRequestStatus
{
    /// <summary>Asked; the kiosk has not answered yet.</summary>
    Pending = 0,

    /// <summary>The kiosk sent what it had of the time asked for (maybe nothing).</summary>
    Delivered = 1,
}

/// <summary>
/// The panel asks a kiosk for the camera minutes of a time span it keeps only
/// on its own disk (T-483): the kiosk sends them, sealed as they are, and the
/// backend keeps them a day for viewing.
/// </summary>
/// <remarks>
/// Not a <see cref="KioskCommand"/>: a command carries no time span. Pushed
/// over <c>/hubs/kiosk</c>; a kiosk that was offline finds it with
/// <c>GET kiosks/me/camera/requests</c> when it comes back, until
/// <see cref="ExpiresAt"/>.
/// </remarks>
public sealed class CameraRequest : Entity
{
    /// <summary>A kiosk that has not answered in this time is no longer asked.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The longest span asked for at once: an hour is sixty minutes to send.</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromHours(1);

    public Guid KioskId { get; set; }

    /// <summary>The span's start, UTC.</summary>
    public DateTime From { get; set; }

    /// <summary>The span's end, UTC.</summary>
    public DateTime To { get; set; }

    public Guid RequestedBy { get; set; }

    public CameraRequestStatus Status { get; private set; } = CameraRequestStatus.Pending;

    /// <summary>How many minutes the kiosk had of the span.</summary>
    public int Delivered { get; private set; }

    public DateTime? DeliveredAt { get; private set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether a minute starting then belongs to the span (it may begin up to a minute before it).</summary>
    public bool Covers(DateTime start) => start > From.AddMinutes(-2) && start < To;

    /// <summary>
    /// The kiosk sent what it had. Said twice - a repeated request - the
    /// first answer stands.
    /// </summary>
    public void Deliver(int minutes, DateTime now)
    {
        if (Status != CameraRequestStatus.Pending)
        {
            return;
        }

        Status = CameraRequestStatus.Delivered;
        Delivered = Math.Max(0, minutes);
        DeliveredAt = now;
    }
}
