using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Security;

namespace NeoPrint.Infrastructure.Seeding;

/// <summary>
/// Development-only data: two kiosk records with an enrollment token each, so a
/// locally built kiosk application activates itself on first start
/// (plan section 12, step 1), and a ready account for every admin role and for
/// the mobile app (docs/DEV_ENVIRONMENT.md).
/// </summary>
/// <remarks>
/// <para>
/// The tokens are written to the log, which is why this never runs outside
/// Development. In production an administrator creates enrollment tokens from
/// the admin panel.
/// </para>
/// <para>
/// The test accounts share one password, <c>NEOPRINT_DEV_TEST_PASSWORD</c>
/// from the seed file, and do not have to change it at first sign-in: they
/// exist to be signed into by anyone testing the stack, with the password the
/// runbook gives. That is a relaxation of plan section 2.8, and it holds only
/// because this class runs only in Development (Program.cs); the first
/// SuperAdmin, seeded in every environment, still has to change its password.
/// No password is ever logged.
/// </para>
/// </remarks>
public sealed class DevelopmentSeeder
{
    /// <summary>The seed value holding the test accounts' shared password.</summary>
    public const string TestPasswordKey = "NEOPRINT_DEV_TEST_PASSWORD";

    /// <summary>The ready-made mobile user, with its email already confirmed.</summary>
    public const string TestUserEmail = "dev-user@neoprint.local";

    /// <summary>One account per admin role template (plan section 8A.2).</summary>
    public static readonly IReadOnlyList<(string Email, string FullName, string Role)> TestAdmins =
    [
        ("dev-superadmin@neoprint.local", "Test SuperAdmin", AdminRole.SuperAdminName),
        ("dev-admin@neoprint.local", "Test Admin", "Admin"),
        ("dev-operator@neoprint.local", "Test Operator", "Operator"),
        ("dev-finance@neoprint.local", "Test Maliyyə", "Maliyyə"),
    ];

    private readonly MongoContext _context;
    private readonly IConfiguration _configuration;
    private readonly IPasswordHasher<AdminUser> _adminHasher;
    private readonly IPasswordHasher<User> _userHasher;
    private readonly ILegalDocumentRepository _legal;
    private readonly ILogger<DevelopmentSeeder> _logger;

    public DevelopmentSeeder(
        MongoContext context,
        IConfiguration configuration,
        IPasswordHasher<AdminUser> adminHasher,
        IPasswordHasher<User> userHasher,
        ILegalDocumentRepository legal,
        ILogger<DevelopmentSeeder> logger)
    {
        _context = context;
        _configuration = configuration;
        _adminHasher = adminHasher;
        _userHasher = userHasher;
        _legal = legal;
        _logger = logger;
    }

    /// <summary>The kiosks created for local work.</summary>
    private static readonly (string Code, string Name, string Address, double Lat, double Lng)[] TestKiosks =
    [
        ("KSK-9001", "Test kiosk — Windows", "Bakı, 28 May metro", 40.3793, 49.8486),
        ("KSK-9002", "Test kiosk — Linux", "Bakı, Elmlər Akademiyası metro", 40.3752, 49.8342),
    ];

    /// <summary>The PIN the hidden admin exit accepts on the test kiosks.</summary>
    private const string TestAdminPin = "135790";

    /// <summary>The online till app sales and top-ups go through locally (plan section 2.12).</summary>
    private const string TestOnlineTerminal = "ONLINE-DEV-01";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedTestAccountsAsync(cancellationToken).ConfigureAwait(false);
        await SeedKiosksAsync(cancellationToken).ConfigureAwait(false);
        await SeedFiscalAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string TestTerminal(string kioskCode) => $"TERM-{kioskCode}";

    /// <summary>Where the refund status QR points locally: the API itself serves the page.</summary>
    private const string TestSiteUrl = "http://localhost:5080";

    /// <summary>The online till, only while nobody has set one; the site address, only while it is the production default.</summary>
    private async Task SeedFiscalAsync(CancellationToken cancellationToken)
    {
        var result = await _context.AdminSettings.UpdateOneAsync(
                Builders<AdminSetting>.Filter.Eq(s => s.Key, SettingKeys.FiscalOnlineTerminalId) & Builders<AdminSetting>.Filter.Eq(s => s.Value, string.Empty),
                Builders<AdminSetting>.Update.Set(s => s.Value, TestOnlineTerminal),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ModifiedCount > 0)
        {
            _logger.LogInformation("Set the development online fiscal terminal {Terminal}.", TestOnlineTerminal);
        }

        await _context.AdminSettings.UpdateOneAsync(
                Builders<AdminSetting>.Filter.Eq(s => s.Key, SettingKeys.PublicSiteUrl) & Builders<AdminSetting>.Filter.Eq(s => s.Value, "https://neoprint.az"),
                Builders<AdminSetting>.Update.Set(s => s.Value, TestSiteUrl),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SeedKiosksAsync(CancellationToken cancellationToken)
    {
        var ttlHours = await ReadEnrollmentTokenTtlHoursAsync(cancellationToken).ConfigureAwait(false);
        var issued = new List<(string Code, string Token)>();

        foreach (var (code, name, address, lat, lng) in TestKiosks)
        {
            var kiosk = await _context.Kiosks
                .Find(k => k.Code == code)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (kiosk is null)
            {
                kiosk = new Kiosk
                {
                    Code = code,
                    Name = name,
                    Location = new KioskLocation { Address = address, Lat = lat, Lng = lng },
                    AdminPinHash = Hashing.HashSecret(TestAdminPin),
                    FiscalTerminalId = TestTerminal(code),
                    PaperStock =
                    {
                        ["A4"] = new PaperStock { Capacity = 500, Current = 500 },
                        ["A3"] = new PaperStock { Capacity = 250, Current = 250 },
                    },
                };

                await _context.Kiosks.InsertOneAsync(kiosk, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation("Seeded development kiosk {Code}.", code);
            }
            else if (string.IsNullOrEmpty(kiosk.FiscalTerminalId))
            {
                // Seeded before fiscal receipts existed (step 4): its sales would wait for a till.
                await _context.Kiosks.UpdateOneAsync(
                        Builders<Kiosk>.Filter.Eq(k => k.Id, kiosk.Id),
                        Builders<Kiosk>.Update.Set(k => k.FiscalTerminalId, TestTerminal(code)),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            // A device may already be bound from an earlier run; do not hand out
            // a second token for a kiosk that is already activated.
            if (kiosk.ActiveDeviceId is not null)
            {
                continue;
            }

            var hasUsableToken = await _context.EnrollmentTokens
                .Find(token => token.KioskId == kiosk.Id
                               && token.UsedByDeviceId == null
                               && token.RevokedAt == null
                               && token.ExpiresAt > DateTime.UtcNow)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);

            if (hasUsableToken)
            {
                continue;
            }

            var raw = EnrollmentToken.NewToken();

            await _context.EnrollmentTokens.InsertOneAsync(
                    new EnrollmentToken
                    {
                        TokenHash = Hashing.HashToken(raw),
                        KioskId = kiosk.Id,
                        ExpiresAt = DateTime.UtcNow.AddHours(ttlHours),
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            issued.Add((code, raw));
        }

        if (issued.Count == 0)
        {
            return;
        }

        // Written as one block so it is easy to copy out of the container log.
        _logger.LogInformation(
            """
            Development enrollment tokens (valid for {Hours} h). Start the kiosk application with one of these
            and it activates itself against that kiosk, with no pairing code to read off the screen:

            {Commands}

            The hidden admin exit on these kiosks accepts PIN {Pin}.
            These are development values and are never seeded outside Development.
            """,
            ttlHours,
            string.Join(
                Environment.NewLine,
                issued.Select(entry =>
                    $"  {entry.Code}:  --enrollment-token {entry.Token}")),
            TestAdminPin);
    }

    /// <summary>
    /// Creates the test accounts that do not exist yet. An account that exists
    /// is left alone - including a password someone changed on purpose.
    /// </summary>
    private async Task SeedTestAccountsAsync(CancellationToken cancellationToken)
    {
        var password = _configuration[TestPasswordKey];

        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "{Key} is not set; no development test accounts were seeded (docs/DEV_ENVIRONMENT.md).",
                TestPasswordKey);
            return;
        }

        var roles = await _context.AdminRoles
            .Find(FilterDefinition<AdminRole>.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var (email, fullName, roleName) in TestAdmins)
        {
            var exists = await _context.AdminUsers
                .Find(admin => admin.Email == email)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);

            // A template the owner deleted or renamed is simply skipped.
            var role = roles.FirstOrDefault(candidate => candidate.Name == roleName);

            if (exists || role is null)
            {
                continue;
            }

            var admin = new AdminUser
            {
                Email = email,
                FullName = fullName,
                RoleIds = [role.Id],
                KioskScope = KioskScope.Everything(),
                IsActive = true,
                MustChangePassword = false,
            };

            admin.PasswordHash = _adminHasher.HashPassword(admin, password);

            await _context.AdminUsers.InsertOneAsync(admin, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Seeded development test account {Email} ({Role}).", email, roleName);
        }

        var userExists = await _context.Users
            .Find(user => user.Email == TestUserEmail)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        // The development user and the checks' throwaway users (*@neoprint.test) are
        // test accounts: what they order at the development kiosks is not the
        // pilot's (T-300). Also on a database that was seeded before the mark.
        await _context.Users
            .UpdateManyAsync(
                Builders<User>.Filter.Eq(user => user.Email, TestUserEmail)
                | Builders<User>.Filter.Regex(user => user.Email, new MongoDB.Bson.BsonRegularExpression(@"@neoprint\.test$")),
                Builders<User>.Update.Set(user => user.IsTestAccount, true),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (userExists)
        {
            return;
        }

        var user = new User
        {
            PublicId = User.NewPublicId(),
            Email = TestUserEmail,
            FullName = "Test User",
            ReferralCode = User.NewReferralCode(),
            EmailConfirmed = true,
            Language = "az",
            IsTestAccount = true,
        };

        user.PasswordHash = _userHasher.HashPassword(user, password);

        await _context.Users.InsertOneAsync(user, cancellationToken: cancellationToken).ConfigureAwait(false);

        // The current documents, accepted, so signing in lands on the home
        // screen rather than on the consent screen.
        var current = await _legal.GetCurrentAsync(user.Language, cancellationToken).ConfigureAwait(false);

        var consents = current.Values
            .Select(document => new Consent
            {
                UserId = user.Id,
                DocType = document.DocType,
                Version = document.Version,
            })
            .ToList();

        if (consents.Count > 0)
        {
            await _legal.AddConsentsAsync(consents, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Seeded development test account {Email} (mobile).", TestUserEmail);
    }

    private async Task<int> ReadEnrollmentTokenTtlHoursAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.AdminSettings
            .Find(s => s.Key == SettingKeys.EnrollmentTokenTtlHours)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return setting is not null && int.TryParse(setting.Value, out var hours) && hours > 0 ? hours : 72;
    }
}
