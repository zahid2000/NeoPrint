namespace NeoPrint.Domain.Admin;

/// <summary>
/// The permission catalogue (plan section 8A.2).
/// </summary>
/// <remarks>
/// Fixed in code and served to the admin panel through
/// <c>GET admin/permissions</c>. Roles are created and edited from the panel,
/// but the set of permissions a role can be granted is defined here, so a typo
/// in the database cannot invent a permission that nothing checks.
/// </remarks>
public static class Permissions
{
    // Dashboard and reporting
    public const string DashboardView = "dashboard.view";
    public const string ReportsView = "reports.view";
    public const string AnalyticsView = "analytics.view";

    // Kiosks
    public const string KiosksView = "kiosks.view";
    public const string KiosksStatusView = "kiosks.status.view";
    public const string KiosksCreate = "kiosks.create";
    public const string KiosksEdit = "kiosks.edit";
    public const string KiosksDelete = "kiosks.delete";
    public const string KiosksActivate = "kiosks.activate";
    public const string DevicesView = "devices.view";
    public const string DevicesRevoke = "devices.revoke";
    public const string DevicesEnroll = "devices.enroll";
    public const string ReleasesView = "releases.view";
    public const string ReleasesManage = "releases.manage";
    public const string KioskCommandsUpdate = "kiosks.commands.update";
    public const string KioskLogsDownload = "kiosks.logs.download";
    public const string KioskLimitsManage = "kiosks.limits.manage";
    public const string KioskCommandsPause = "kiosks.commands.pause";
    public const string KioskCommandsRestart = "kiosks.commands.restart";
    public const string KioskCommandsReboot = "kiosks.commands.reboot";

    // Printers
    public const string PrintersView = "printers.view";
    public const string PrintersSelect = "printers.select";
    public const string PrinterCapabilitiesManage = "printers.capabilities.manage";
    public const string PrintersTestPrint = "printers.testprint";
    public const string PrintersRefresh = "printers.refresh";

    // Supplies and service
    public const string SuppliesView = "supplies.view";
    public const string PaperRefill = "paper.refill";
    public const string SupplyRulesManage = "supply.rules.manage";
    public const string ServiceTasksView = "service.tasks.view";
    public const string ServiceTasksManage = "service.tasks.manage";
    public const string ServiceTasksComplete = "service.tasks.complete";
    public const string InventoryView = "inventory.view";
    public const string InventoryManage = "inventory.manage";

    // Operator panel
    public const string OperatorPanel = "operator.panel";

    // Queue and jobs
    public const string JobsView = "jobs.view";
    public const string JobsCancel = "jobs.cancel";

    // Payments
    public const string PaymentsView = "payments.view";
    public const string PaymentsRefund = "payments.refund";
    public const string RefundsView = "refunds.view";
    public const string RefundsRetry = "refunds.retry";
    public const string ReceiptsView = "receipts.view";
    public const string VouchersView = "vouchers.view";
    public const string FiscalView = "fiscal.view";
    public const string FiscalManage = "fiscal.manage";

    // Users
    public const string UsersView = "users.view";
    public const string UsersBlock = "users.block";
    public const string WalletsAdjust = "wallets.adjust";

    // Configuration
    public const string PricingManage = "pricing.manage";
    public const string SettingsManage = "settings.manage";
    public const string PromosManage = "promos.manage";
    public const string ReferralsView = "referrals.view";
    public const string FormatsManage = "formats.manage";
    public const string FeaturesManage = "features.manage";
    public const string LegalManage = "legal.manage";

    // Incidents
    public const string IncidentsView = "incidents.view";
    public const string IncidentsAcknowledge = "incidents.acknowledge";
    public const string IncidentsResolve = "incidents.resolve";

    // System
    public const string StorageManage = "storage.manage";
    public const string AdminsManage = "admins.manage";
    public const string RolesManage = "roles.manage";
    public const string AuditView = "audit.view";
    public const string SystemJobsView = "system.jobs.view";

    /// <summary>The catalogue, grouped as the admin panel displays it.</summary>
    public static readonly IReadOnlyList<PermissionGroup> Catalogue =
    [
        new("dashboard", "Dashboard / hesabat", [DashboardView, ReportsView, AnalyticsView]),
        new("kiosks", "Kiosklar",
        [
            KiosksView, KiosksStatusView, KiosksCreate, KiosksEdit, KiosksDelete, KiosksActivate,
            DevicesView, DevicesRevoke, DevicesEnroll, ReleasesView, ReleasesManage,
            KioskCommandsUpdate, KioskLogsDownload, KioskLimitsManage,
            KioskCommandsPause, KioskCommandsRestart, KioskCommandsReboot,
        ]),
        new("printers", "Printerlər",
        [
            PrintersView, PrintersSelect, PrinterCapabilitiesManage, PrintersTestPrint, PrintersRefresh,
        ]),
        new("supplies", "Sərf / servis",
        [
            SuppliesView, PaperRefill, SupplyRulesManage, ServiceTasksView, ServiceTasksManage,
            ServiceTasksComplete, InventoryView, InventoryManage,
        ]),
        new("operator", "Operator paneli", [OperatorPanel]),
        new("jobs", "Queue / job-lar", [JobsView, JobsCancel]),
        new("payments", "Ödənişlər",
        [
            PaymentsView, PaymentsRefund, RefundsView, RefundsRetry, ReceiptsView, VouchersView,
            FiscalView, FiscalManage,
        ]),
        new("users", "İstifadəçilər", [UsersView, UsersBlock, WalletsAdjust]),
        new("config", "Konfiqurasiya",
        [
            PricingManage, SettingsManage, PromosManage, ReferralsView, FormatsManage,
            FeaturesManage, LegalManage,
        ]),
        new("incidents", "Incident-lər", [IncidentsView, IncidentsAcknowledge, IncidentsResolve]),
        new("system", "Sistem", [StorageManage, AdminsManage, RolesManage, AuditView, SystemJobsView]),
    ];

    /// <summary>Every permission in the catalogue.</summary>
    public static readonly IReadOnlySet<string> All =
        Catalogue.SelectMany(group => group.Permissions).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a string names a permission this build knows about.</summary>
    public static bool IsKnown(string permission) => All.Contains(permission);
}

/// <summary>A group of permissions as shown in the role editor.</summary>
/// <param name="Key">Stable key, for the UI translation files.</param>
/// <param name="Title">Default (Azerbaijani) group title.</param>
/// <param name="Permissions">Permissions in the group.</param>
public sealed record PermissionGroup(string Key, string Title, IReadOnlyList<string> Permissions);
