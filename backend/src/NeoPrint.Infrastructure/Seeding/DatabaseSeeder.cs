using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Features;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Supplies;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Seeding;

/// <summary>
/// Brings an empty database up to a usable state (plan section 12, step 1).
/// </summary>
/// <remarks>
/// Idempotent: it only creates what is missing and never overwrites something
/// an administrator has since changed. That matters because it runs on every
/// start, including after a deployment that adds a new setting or flag.
/// </remarks>
public sealed class DatabaseSeeder
{
    private readonly MongoContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseSeeder> _logger;
    private readonly IPasswordHasher<AdminUser> _passwordHasher;

    public DatabaseSeeder(
        MongoContext context,
        IConfiguration configuration,
        ILogger<DatabaseSeeder> logger,
        IPasswordHasher<AdminUser> passwordHasher)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _passwordHasher = passwordHasher;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedSettingsAsync(cancellationToken).ConfigureAwait(false);
        await SeedRolesAsync(cancellationToken).ConfigureAwait(false);
        await SeedSuperAdminAsync(cancellationToken).ConfigureAwait(false);
        await SeedPricingProfileAsync(cancellationToken).ConfigureAwait(false);
        await SeedFeatureFlagsAsync(cancellationToken).ConfigureAwait(false);
        await SeedSupplyAlertRulesAsync(cancellationToken).ConfigureAwait(false);
        await SeedLegalDocumentsAsync(cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------------

    private async Task SeedSettingsAsync(CancellationToken cancellationToken)
    {
        var existing = await _context.AdminSettings
            .Find(FilterDefinition<AdminSetting>.Empty)
            .Project(setting => setting.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var known = existing.ToHashSet(StringComparer.Ordinal);

        var missing = SettingKeys.Defaults
            .Where(definition => !known.Contains(definition.Key))
            .Select(definition => new AdminSetting
            {
                Key = definition.Key,
                Value = definition.DefaultValue,
                DefaultValue = definition.DefaultValue,
                ValueType = definition.ValueType,
                Description = definition.Description,
            })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        await _context.AdminSettings.InsertManyAsync(missing, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Seeded {Count} settings.", missing.Count);
    }

    // -----------------------------------------------------------------------
    // Roles
    // -----------------------------------------------------------------------

    /// <summary>
    /// The role templates from plan section 8A.2. Everything except SuperAdmin
    /// can be edited or deleted afterwards.
    /// </summary>
    private static IEnumerable<AdminRole> RoleTemplates()
    {
        yield return new AdminRole
        {
            Name = AdminRole.SuperAdminName,
            Description = "Bütün icazələr. Sistem rolu — redaktə və silinə bilməz.",
            IsSystem = true,
            Permissions = [.. Permissions.All],
        };

        yield return new AdminRole
        {
            Name = "Admin",
            Description = "Sistem idarəsi (storage, admin istifadəçiləri, rollar, audit) xaric hamısı.",
            Permissions =
            [
                .. Permissions.All.Except(
                [
                    Permissions.StorageManage,
                    Permissions.AdminsManage,
                    Permissions.RolesManage,
                    Permissions.AuditView,

                    // Given by hand, also on a new install (T-284).
                    Permissions.SystemJobsView,

                    // What leaves the reports is a SuperAdmin's to choose, or given by hand (T-300).
                    Permissions.UsersMarkTest,
                ], StringComparer.Ordinal),
            ],
        };

        yield return new AdminRole
        {
            Name = "Operator",
            Description = "Sahədə kiosklara xidmət edən texniki işçi.",
            Permissions =
            [
                Permissions.OperatorPanel, Permissions.DashboardView,
                Permissions.KiosksView, Permissions.KiosksActivate, Permissions.DevicesView,
                Permissions.KiosksStatusView,
                Permissions.KioskCommandsPause, Permissions.KioskCommandsRestart,
                Permissions.KioskCommandsReboot, Permissions.KioskCommandsUpdate,
                Permissions.PrintersView, Permissions.PrintersTestPrint, Permissions.PrintersRefresh,
                Permissions.SuppliesView, Permissions.PaperRefill,
                Permissions.ServiceTasksView, Permissions.ServiceTasksComplete,
                Permissions.InventoryView, Permissions.JobsView,
                Permissions.IncidentsView, Permissions.IncidentsAcknowledge, Permissions.IncidentsResolve,
            ],
        };

        yield return new AdminRole
        {
            Name = "Maliyyə",
            Description = "Ödəniş, qaytarma, qəbz və fiskal çeklər.",
            Permissions =
            [
                Permissions.DashboardView, Permissions.ReportsView,
                Permissions.PaymentsView, Permissions.PaymentsRefund,
                Permissions.RefundsView, Permissions.RefundsRetry,
                Permissions.ReceiptsView, Permissions.VouchersView,
                Permissions.FiscalView, Permissions.FiscalManage,
                Permissions.UsersView, Permissions.WalletsAdjust, Permissions.JobsView,
            ],
        };
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        foreach (var template in RoleTemplates())
        {
            var existing = await _context.AdminRoles
                .Find(role => role.Name == template.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                await _context.AdminRoles.InsertOneAsync(template, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation("Seeded role '{Role}'.", template.Name);
                continue;
            }

            // SuperAdmin must keep every permission, including ones added by a
            // later release. The editable roles are left exactly as the
            // administrator has them.
            if (existing.IsSuperAdmin && !Permissions.All.SetEquals(existing.Permissions))
            {
                existing.Permissions = [.. Permissions.All];
                existing.UpdatedAt = DateTime.UtcNow;

                await _context.AdminRoles
                    .ReplaceOneAsync(role => role.Id == existing.Id, existing, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("Refreshed the SuperAdmin permission set.");
            }
        }
    }

    // -----------------------------------------------------------------------
    // First administrator
    // -----------------------------------------------------------------------

    private async Task SeedSuperAdminAsync(CancellationToken cancellationToken)
    {
        var email = _configuration["NEOPRINT_SEED_ADMIN_EMAIL"];
        var password = _configuration["NEOPRINT_SEED_ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "NEOPRINT_SEED_ADMIN_EMAIL or NEOPRINT_SEED_ADMIN_PASSWORD is not set; no administrator was seeded.");
            return;
        }

        email = User.NormalizeEmail(email);

        var alreadyExists = await _context.AdminUsers
            .Find(admin => admin.Email == email)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (alreadyExists)
        {
            return;
        }

        var superAdmin = await _context.AdminRoles
            .Find(role => role.Name == AdminRole.SuperAdminName)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        var admin = new AdminUser
        {
            Email = email,
            FullName = "Super Admin",
            RoleIds = [superAdmin.Id],
            KioskScope = KioskScope.Everything(),
            IsActive = true,
            // Plan section 2.8: the seeded password must be changed at first
            // sign-in, because it came from a configuration value.
            MustChangePassword = true,
        };

        admin.PasswordHash = _passwordHasher.HashPassword(admin, password);

        await _context.AdminUsers.InsertOneAsync(admin, cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Seeded the first SuperAdmin ({Email}). The password must be changed at first sign-in.", email);
    }

    // -----------------------------------------------------------------------
    // Pricing
    // -----------------------------------------------------------------------

    private async Task SeedPricingProfileAsync(CancellationToken cancellationToken)
    {
        var hasDefault = await _context.PricingProfiles
            .Find(profile => profile.IsDefault)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (hasDefault)
        {
            return;
        }

        // The default values from plan section 2.3.
        var profile = new PricingProfile
        {
            Name = PricingProfile.DefaultProfileName,
            IsDefault = true,
        };

        await _context.PricingProfiles.InsertOneAsync(profile, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Seeded the default pricing profile.");
    }

    // -----------------------------------------------------------------------
    // Feature flags
    // -----------------------------------------------------------------------

    private async Task SeedFeatureFlagsAsync(CancellationToken cancellationToken)
    {
        var existing = await _context.FeatureFlags
            .Find(FilterDefinition<FeatureFlag>.Empty)
            .Project(flag => flag.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var known = existing.ToHashSet(StringComparer.Ordinal);

        var missing = FeatureFlag.MvpFlags
            .Where(flag => !known.Contains(flag.Key))
            .Select(flag => new FeatureFlag
            {
                Key = flag.Key,
                Description = flag.Description,
                // Plan section 2.16: every MVP flag is on by default and can be
                // turned off per kiosk during the pilot.
                DefaultEnabled = true,
                Platforms = [.. flag.Platforms],
                RolloutPercent = 100,
            })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        await _context.FeatureFlags.InsertManyAsync(missing, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Seeded {Count} feature flags.", missing.Count);
    }

    // -----------------------------------------------------------------------
    // Supply alert rules
    // -----------------------------------------------------------------------

    private async Task SeedSupplyAlertRulesAsync(CancellationToken cancellationToken)
    {
        var alreadySeeded = await _context.SupplyAlertRules
            .Find(FilterDefinition<SupplyAlertRule>.Empty)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (alreadySeeded)
        {
            return;
        }

        // The seed rules from plan section 2.15.
        var rules = new List<SupplyAlertRule>
        {
            new()
            {
                Name = "Kağız A4",
                Resource = SupplyResource.PaperA4,
                Unit = SupplyUnit.Sheets,
                WarningThreshold = 200,
                CriticalThreshold = 50,
                Channels = AlertChannels.AdminPanel | AlertChannels.OperatorPanel,
                CreateServiceTask = true,
                TaskPriority = TaskPriority.High,
            },
            new()
            {
                Name = "Toner",
                Resource = SupplyResource.Toner,
                Unit = SupplyUnit.Percent,
                WarningThreshold = 15,
                CriticalThreshold = 3,
                Channels = AlertChannels.AdminPanel | AlertChannels.OperatorPanel,
                CreateServiceTask = true,
                TaskPriority = TaskPriority.High,
            },
        };

        await _context.SupplyAlertRules.InsertManyAsync(rules, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Seeded {Count} supply alert rules.", rules.Count);
    }

    // -----------------------------------------------------------------------
    // Legal documents
    // -----------------------------------------------------------------------

    private async Task SeedLegalDocumentsAsync(CancellationToken cancellationToken)
    {
        foreach (var (docType, title) in new[]
                 {
                     (LegalDocumentType.Privacy, "Məxfilik siyasəti"),
                     (LegalDocumentType.Terms, "İstifadə şərtləri"),
                     (LegalDocumentType.ScanNotice, "Sənəd skanı barədə məlumat"),
                 })
        {
            var exists = await _context.LegalDocuments
                .Find(document => document.DocType == docType && document.Language == "az")
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                continue;
            }

            await _context.LegalDocuments.InsertOneAsync(
                    new LegalDocument
                    {
                        DocType = docType,
                        Version = LegalDocumentDrafts.DraftVersion,
                        Language = "az",
                        Content = LegalDocumentDrafts.For(docType),
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Seeded the {Title} draft.", title);
        }
    }
}
