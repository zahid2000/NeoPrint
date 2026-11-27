using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// One minute of a kiosk camera's recording kept on a file server (T-485):
/// the sealed file exactly as the kiosk made it - the backend's camera key
/// opens it - in the storage provider the panel chose, until
/// <see cref="ExpiresAt"/>.
/// </summary>
public sealed class CameraSegment : Entity
{
    public Guid KioskId { get; set; }

    /// <summary>When the minute starts (UTC); with the kiosk, what makes it unique.</summary>
    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    /// <summary>The sealed size.</summary>
    public long Bytes { get; set; }

    public Guid ProviderId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 of the stored bytes, hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The backend camera key it is sealed for.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>When it is deleted (<c>Camera.ArchiveRetentionDays</c> after it was recorded).</summary>
    public DateTime ExpiresAt { get; set; }
}
