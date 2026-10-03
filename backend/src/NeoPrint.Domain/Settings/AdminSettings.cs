using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Settings;

/// <summary>
/// One tunable setting (plan section 2.4, <c>admin_settings</c>).
/// </summary>
/// <remarks>
/// Values are stored as strings with a declared type, so the admin panel can
/// render the right input and offer "reset to default" without a schema
/// migration each time a setting is added.
/// </remarks>
public sealed class AdminSetting : Entity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string DefaultValue { get; set; } = string.Empty;

    public SettingValueType ValueType { get; set; } = SettingValueType.String;

    public string? Description { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsDefault => string.Equals(Value, DefaultValue, StringComparison.Ordinal);
}

public enum SettingValueType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    /// <summary>Comma-separated list, used for the allowed file formats.</summary>
    StringList = 4,
    /// <summary>Cron expression.</summary>
    Cron = 5,
    /// <summary>Time range such as <c>02:00-05:00</c>.</summary>
    TimeRange = 6,
}

/// <summary>
/// The setting keys and their defaults (plan section 2.4).
/// </summary>
/// <remarks>
/// Defined in code so that the seeder can create anything missing and the admin
/// panel can always offer the documented default, even for a setting added in a
/// later release.
/// </remarks>
public static class SettingKeys
{
    public const string QueueDisplayLimit = "Queue.DisplayLimit";
    public const string PaymentTimeoutMinutes = "Payment.TimeoutMinutes";
    public const string KioskSessionTtlMinutes = "KioskSession.TtlMinutes";
    public const string KioskIdleResetSeconds = "Kiosk.IdleResetSeconds";
    public const string UserQrRotationSeconds = "UserQr.RotationSeconds";

    public const string PreparedPrintAutoCancelDays = "PreparedPrint.AutoCancelDays";
    public const string ScheduleReminderLeadMinutes = "Schedule.ReminderLeadMinutes";
    public const string ScheduleAutoCancelHours = "Schedule.AutoCancelHours";
    public const string ScheduleMaxDaysAhead = "Schedule.MaxDaysAhead";
    public const string ReferralBonusPages = "Referral.BonusPages";
    public const string ReferralInviteeBonusPages = "Referral.InviteeBonusPages";
    public const string ReferralMaxBonusPagesPerJob = "Referral.MaxBonusPagesPerJob";
    public const string RefundVoucherValidDays = "Refund.VoucherValidDays";
    public const string RefundProviderMaxAttempts = "Refund.ProviderMaxAttempts";
    public const string JobSuspectedAfterMinutes = "Job.SuspectedAfterMinutes";
    public const string JobSuspectedAutoResolveHours = "Job.SuspectedAutoResolveHours";
    public const string PublicSiteUrl = "Public.SiteUrl";
    public const string KioskHeartbeatSeconds = "Kiosk.HeartbeatSeconds";
    public const string KioskOfflineAfterSeconds = "Kiosk.OfflineAfterSeconds";
    public const string KioskTelemetrySeconds = "Kiosk.TelemetrySeconds";
    public const string KioskTelemetryRetentionDays = "Kiosk.TelemetryRetentionDays";

    /// <summary>
    /// How long a kiosk may act on its cached configuration once the backend is
    /// unreachable. Past this it goes Degraded and stops taking orders
    /// (plan sections 2.4, 7.3).
    /// </summary>
    public const string KioskConfigCacheTtlHours = "Kiosk.ConfigCacheTtlHours";

    /// <summary>
    /// How long after a payment expires its real state is reconciled with the
    /// provider, for a callback that arrives late (plan sections 2.4, 2.5).
    /// </summary>
    public const string PaymentReconcileAfterMinutes = "Payment.ReconcileAfterMinutes";

    /// <summary>
    /// How long a recorded state transition is kept
    /// (<c>docs/DATA_RETENTION.md</c>).
    /// </summary>
    /// <remarks>
    /// A year: long enough to answer "what happened to this kiosk last
    /// season", short enough that the collection does not grow without bound.
    /// Not financial data, so it is not covered by the rules that keep
    /// payments (plan section 14).
    /// </remarks>
    public const string RetentionStateTransitionDays = "Retention.StateTransitionDays";

    /// <summary>
    /// How long a processed provider callback is kept.
    /// </summary>
    /// <remarks>
    /// Six months. The record exists to refuse a duplicate delivery, and no
    /// provider resends a webhook that long after the event; the payment
    /// itself is kept under its own rules.
    /// </remarks>
    public const string RetentionProviderEventDays = "Retention.ProviderEventDays";
    public const string FilesDefaultMaxSizeMb = "Files.DefaultMaxSizeMb";
    public const string FilesDefaultMaxPages = "Files.DefaultMaxPages";
    public const string FilesUserQuotaGb = "Files.UserQuotaGb";
    public const string FilesAllowedFormats = "Files.AllowedFormats";
    public const string FilesVersionsToKeep = "Files.VersionsToKeep";
    public const string StorageSyncParallelism = "Storage.SyncParallelism";
    public const string StorageSourceRetentionDays = "Storage.SourceRetentionDays";
    public const string StorageReconciliationCron = "Storage.ReconciliationCron";
    public const string LayoutDefaultMarginMm = "Layout.DefaultMarginMm";
    public const string PrivacySensitiveAutoDeleteDefault = "Privacy.SensitiveAutoDeleteDefault";
    public const string PrivacyAccountDeletionGraceHours = "Privacy.AccountDeletionGraceHours";
    public const string FiscalEnabled = "Fiscal.Enabled";
    public const string FiscalTopUpReceiptMode = "Fiscal.TopUpReceiptMode";
    public const string FiscalMaxPendingMinutes = "Fiscal.MaxPendingMinutes";
    public const string PaperBlockWhenInsufficient = "Paper.BlockWhenInsufficient";
    public const string ServiceForecastLeadHours = "Service.ForecastLeadHours";
    public const string ServiceTaskOverdueEscalationMinutes = "Service.TaskOverdueEscalationMinutes";
    public const string AnalyticsRetentionDays = "Analytics.RetentionDays";
    public const string FiscalOnlineTerminalId = "Fiscal.OnlineTerminalId";
    public const string KioskUpdateWindow = "Kiosk.UpdateWindow";
    public const string UpdateCheckIntervalMinutes = "Update.CheckIntervalMinutes";
    public const string UpdateHealthTimeoutMinutes = "Update.HealthTimeoutMinutes";
    public const string UpdateKeepVersions = "Update.KeepVersions";
    public const string EnrollmentTokenTtlHours = "Enrollment.TokenTtlHours";
    public const string KioskCrashLoopThreshold = "Kiosk.CrashLoopThreshold";

    /// <summary>The MVP file formats (plan section 2.2).</summary>
    public const string DefaultAllowedFormats =
        "pdf,doc,docx,docm,dot,dotx,odt,ott,rtf,wps,txt,html,htm,xml," +
        "xls,xlsx,xlsm,xltx,ods,csv," +
        "ppt,pptx,pps,ppsx,potx,odp," +
        "jpg,jpeg,png,bmp,gif,webp," +
        "ps,eps,pcl,prn,xps,oxps";

    /// <summary>Every setting with its documented default.</summary>
    public static readonly IReadOnlyList<SettingDefinition> Defaults =
    [
        new(QueueDisplayLimit, "5", SettingValueType.Integer, "Kioskda göstərilən queue sayı (3 və ya 5)"),
        new(PaymentTimeoutMinutes, "5", SettingValueType.Integer, "Ödənilməmiş intent-in bitmə vaxtı"),
        new(KioskSessionTtlMinutes, "10", SettingValueType.Integer, "Mobil ↔ kiosk sessiyasının fəaliyyətsizlik müddəti"),
        new(KioskIdleResetSeconds, "90", SettingValueType.Integer, "Toxunulmasa ana ekrana qayıdış"),
        new(UserQrRotationSeconds, "60", SettingValueType.Integer, "Şəxsi QR token-in yenilənmə müddəti"),
        new(PreparedPrintAutoCancelDays, "7", SettingValueType.Integer, "Tətbiqdə əvvəlcədən ödənilmiş, kioskda götürülməyən sifariş neçə gündən sonra ləğv olunur (pul balansa qayıdır)"),
        new(ScheduleReminderLeadMinutes, "0", SettingValueType.Integer, "Planlanmış vaxtda bildiriş"),
        new(ScheduleAutoCancelHours, "24", SettingValueType.Integer, "Cavab verilməyən planlanmış çapın avtomatik ləğvi"),
        new(ScheduleMaxDaysAhead, "30", SettingValueType.Integer, "Çapı ən çox neçə gün sonraya planlamaq olar"),
        new(ReferralBonusPages, "10", SettingValueType.Integer, "Dəvət edənə verilən bonus səhifə"),
        new(ReferralInviteeBonusPages, "0", SettingValueType.Integer, "Dəvət olunana verilən bonus"),
        new(ReferralMaxBonusPagesPerJob, "5", SettingValueType.Integer, "Bir çapda maksimum bonus səhifə"),
        new(RefundVoucherValidDays, "90", SettingValueType.Integer, "Anonim refund voucher-in etibarlılığı"),
        new(RefundProviderMaxAttempts, "3", SettingValueType.Integer, "Bank/kart refund-unun təkrar cəhd sayı"),
        new(JobSuspectedAfterMinutes, "15", SettingValueType.Integer, "Kioskdan bu qədər dəqiqə xəbər gəlməsə çap/növbə job-u Suspected olur (avtomatik refund yoxdur)"),
        new(JobSuspectedAutoResolveHours, "72", SettingValueType.Integer, "Həll olunmamış Suspected job üçün adminə xatırlatma (avtomatik heç nə edilmir)"),
        new(PublicSiteUrl, "https://neoprint.az", SettingValueType.String, "QR kodlardakı ictimai səhifələrin ünvanı (refund statusu)"),
        new(KioskHeartbeatSeconds, "15", SettingValueType.Integer, "Kiosk heartbeat intervalı"),
        new(KioskOfflineAfterSeconds, "60", SettingValueType.Integer, "Heartbeat gəlməzsə offline sayılır"),
        new(KioskTelemetrySeconds, "30", SettingValueType.Integer, "Telemetriya göndərmə intervalı"),
        new(KioskTelemetryRetentionDays, "30", SettingValueType.Integer, "Telemetriya tarixçəsinin saxlanma müddəti"),
        new(KioskConfigCacheTtlHours, "24", SettingValueType.Integer,
            "Kioskun lokal konfiqurasiya keşinin etibarlılığı; köhnədirsə kiosk Degraded rejimə keçir"),
        new(PaymentReconcileAfterMinutes, "10", SettingValueType.Integer,
            "Expired / naməlum ödənişlərin provayder statusu ilə uzlaşdırılması"),
        new(RetentionStateTransitionDays, "365", SettingValueType.Integer,
            "State keçid tarixçəsinin saxlanma müddəti (gün)"),
        new(RetentionProviderEventDays, "180", SettingValueType.Integer,
            "İşlənmiş provayder callback qeydlərinin saxlanma müddəti (gün)"),
        new(FilesDefaultMaxSizeMb, "50", SettingValueType.Integer, "Default maksimum fayl ölçüsü (MB)"),
        new(FilesDefaultMaxPages, "200", SettingValueType.Integer, "Default maksimum səhifə sayı"),
        new(FilesUserQuotaGb, "1", SettingValueType.Integer, "İstifadəçi başına saxlama kvotası (GB)"),
        new(FilesAllowedFormats, DefaultAllowedFormats, SettingValueType.StringList, "İcazəli fayl formatları"),
        new(FilesVersionsToKeep, "5", SettingValueType.Integer, "Saxlanılan redaktə versiyalarının sayı"),
        new(StorageSyncParallelism, "4", SettingValueType.Integer, "Sinxronizasiyada paralel obyekt sayı"),
        new(StorageSourceRetentionDays, "30", SettingValueType.Integer, "Migrasiyadan sonra mənbədə saxlanma müddəti"),
        new(StorageReconciliationCron, "0 3 * * *", SettingValueType.Cron, "Mirror rejimində gecəlik uyğunlaşdırma"),
        new(LayoutDefaultMarginMm, "10", SettingValueType.Integer, "Tərtibat redaktorunda default kənar boşluq (mm)"),
        new(PrivacySensitiveAutoDeleteDefault, "AfterPrint", SettingValueType.String, "Həssas skanlar üçün default avtomatik silinmə"),
        new(PrivacyAccountDeletionGraceHours, "24", SettingValueType.Integer, "Hesab silindikdən sonra faylların fiziki silinmə müddəti"),
        new(FiscalEnabled, "true", SettingValueType.Boolean, "Fiskallaşdırma aktivdir"),
        new(FiscalTopUpReceiptMode, "OnTopUp", SettingValueType.String, "Balans artırılanda (OnTopUp) və ya xidmətdə (OnService) fiskal çek"),
        new(FiscalMaxPendingMinutes, "15", SettingValueType.Integer, "Fiskallaşmayan satış üçün incident həddi"),
        new(PaperBlockWhenInsufficient, "true", SettingValueType.Boolean, "Kağız çatmayanda sifariş qəbul edilmir"),
        new(ServiceForecastLeadHours, "24", SettingValueType.Integer, "Proqnoza görə avtomatik servis tapşırığı"),
        new(ServiceTaskOverdueEscalationMinutes, "60", SettingValueType.Integer, "Critical tapşırığın eskalasiya müddəti"),
        new(AnalyticsRetentionDays, "180", SettingValueType.Integer, "Analitika hadisələrinin saxlanma müddəti"),
        new(FiscalOnlineTerminalId, "", SettingValueType.String, "Mobil (onlayn) satışlar üçün fiskal terminal ID"),
        new(KioskUpdateWindow, "02:00-05:00", SettingValueType.TimeRange, "Kiosk avtomatik yenilənməsinin icazəli saat aralığı"),
        new(UpdateCheckIntervalMinutes, "15", SettingValueType.Integer, "Agent-in yeni versiya yoxlama intervalı"),
        new(UpdateHealthTimeoutMinutes, "5", SettingValueType.Integer, "Yeniləmədən sonra sağlamlıq gözləmə müddəti"),
        new(UpdateKeepVersions, "3", SettingValueType.Integer, "Kioskda saxlanılan versiya sayı"),
        new(EnrollmentTokenTtlHours, "72", SettingValueType.Integer, "Enrollment token-inin etibarlılıq müddəti"),
        new(KioskCrashLoopThreshold, "3", SettingValueType.Integer, "10 dəqiqədə çöküş həddi → təhlükəsiz rejim"),
    ];
}

/// <summary>A setting key with its documented default.</summary>
public sealed record SettingDefinition(string Key, string DefaultValue, SettingValueType ValueType, string Description);
