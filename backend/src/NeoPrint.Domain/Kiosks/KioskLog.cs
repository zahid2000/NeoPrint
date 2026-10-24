using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// Logs a kiosk sent on an <c>UploadLogs</c> command (plan section 7.12;
/// docs/KIOSK_UPDATES.md section 6; T-321): one compressed file, cleaned of
/// personal data, kept encrypted in storage until <see cref="ExpiresAt"/>.
/// </summary>
public sealed class KioskLog : Entity
{
    public Guid KioskId { get; set; }

    /// <summary>The <c>UploadLogs</c> command it answers, when the kiosk named one.</summary>
    public Guid? CommandId { get; set; }

    /// <summary>The compressed size as kept.</summary>
    public long Size { get; set; }

    public int Lines { get; set; }

    /// <summary>Values the backend still had to take out (the kiosk takes them out first).</summary>
    public int Redacted { get; set; }

    public Guid ProviderId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 of the encrypted bytes in the store.</summary>
    public string StoredSha256 { get; set; } = string.Empty;

    public long EncryptedLength { get; set; }

    public string KekId { get; set; } = string.Empty;

    /// <summary>When it is deleted (<c>Logs.RetentionDays</c>).</summary>
    public DateTime ExpiresAt { get; set; }
}
