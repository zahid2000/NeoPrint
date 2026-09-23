using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Security;

namespace NeoPrint.Infrastructure.Seeding;

/// <summary>
/// Development-only data: two kiosk records with an enrollment token each, so a
/// locally built kiosk application activates itself on first start
/// (plan section 12, step 1).
/// </summary>
/// <remarks>
/// The tokens are written to the log, which is why this never runs outside
/// Development. In production an administrator creates enrollment tokens from
/// the admin panel.
/// </remarks>
public sealed class DevelopmentSeeder
{
    private readonly MongoContext _context;
    private readonly ILogger<DevelopmentSeeder> _logger;

    public DevelopmentSeeder(MongoContext context, ILogger<DevelopmentSeeder> logger)
    {
        _context = context;
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

    public async Task SeedAsync(CancellationToken cancellationToken = default)
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
                    Status = KioskStatus.NotActivated,
                    AdminPinHash = Hashing.HashSecret(TestAdminPin),
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

    private async Task<int> ReadEnrollmentTokenTtlHoursAsync(CancellationToken cancellationToken)
    {
        var setting = await _context.AdminSettings
            .Find(s => s.Key == SettingKeys.EnrollmentTokenTtlHours)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return setting is not null && int.TryParse(setting.Value, out var hours) && hours > 0 ? hours : 72;
    }
}
