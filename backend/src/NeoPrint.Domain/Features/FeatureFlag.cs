using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Features;

/// <summary>Which application a flag applies to.</summary>
public enum FeaturePlatform
{
    Kiosk = 0,
    Mobile = 1,
    Admin = 2,
    Backend = 3,
}

/// <summary>
/// A feature flag (plan section 2.16, <c>feature_flags</c>).
/// </summary>
/// <remarks>
/// Evaluated on the backend and handed to each client, so a flag can be turned
/// off for one kiosk during the pilot without shipping anything.
/// </remarks>
public sealed class FeatureFlag : Entity
{
    public string Key { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool DefaultEnabled { get; set; } = true;

    public List<FeaturePlatform> Platforms { get; set; } = [];

    /// <summary>Per-kiosk overrides, keyed by kiosk id.</summary>
    public Dictionary<string, bool> KioskOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Users who always get the feature, whatever the rollout says.</summary>
    public List<Guid> UserAllowlist { get; set; } = [];

    /// <summary>Percentage of mobile users the feature is rolled out to, 0-100.</summary>
    public int RolloutPercent { get; set; } = 100;

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The MVP flags; all default to on (plan section 2.16).</summary>
    public static readonly IReadOnlyList<(string Key, string Description, FeaturePlatform[] Platforms)> MvpFlags =
    [
        ("mobile_payment_at_kiosk", "Kioskda NeoPrint mobil tətbiqi ilə ödəniş", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("user_qr_print", "Şəxsi QR ilə kioskda çap", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("mobile_session_print", "Mobil sessiya ilə çap", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("scheduled_print", "Planlanmış çap", [FeaturePlatform.Mobile, FeaturePlatform.Backend]),
        ("file_sharing", "Fayl paylaşımı", [FeaturePlatform.Mobile, FeaturePlatform.Backend]),
        ("referral", "Referral proqramı", [FeaturePlatform.Mobile, FeaturePlatform.Backend]),
        ("promo_codes", "Promokodlar", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("layout_editor", "Səhifə tərtibatı redaktoru", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("document_scan", "Mobil sənəd skanı", [FeaturePlatform.Mobile]),
        ("txt_editor", "TXT redaktoru", [FeaturePlatform.Kiosk, FeaturePlatform.Mobile]),
        ("data_export", "Məlumatlarımı yüklə", [FeaturePlatform.Mobile]),
    ];
}
