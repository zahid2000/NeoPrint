using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Configuration;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.UserQr;

using QRCoder;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.UserQr;

/// <inheritdoc cref="IUserQrSigner" />
/// <remarks>
/// The key comes from OpenBao through configuration and is read on each call,
/// so a rotated key takes effect without a restart (as the JWT signing key).
/// Rotating it invalidates the QR codes on screen; the app fetches new ones
/// within <c>UserQr.RotationSeconds</c>.
/// </remarks>
public sealed class HmacUserQrSigner : IUserQrSigner
{
    private readonly IConfiguration _configuration;

    public HmacUserQrSigner(IConfiguration configuration) => _configuration = configuration;

    public byte[] Sign(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var value = _configuration["NEOPRINT_QR_HMAC_KEY"];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("NEOPRINT_QR_HMAC_KEY is not configured; personal QR codes cannot be made (docs/OPENBAO.md).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            key = Encoding.UTF8.GetBytes(value);
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException("NEOPRINT_QR_HMAC_KEY is shorter than 256 bits.");
        }

        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload));
    }
}

/// <inheritdoc cref="IQrImages" />
public sealed class QrCoderImages : IQrImages
{
    public byte[] Png(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);

        return new PngByteQRCode(data).GetGraphic(10, drawQuietZones: true);
    }
}

/// <inheritdoc cref="IUserQrGrantStore" />
/// <remarks><c>userqr:{grant}</c>, expiring with the grant. Lost with Redis, the user shows the QR again.</remarks>
public sealed class RedisUserQrGrantStore : IUserQrGrantStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly IClock _clock;

    public RedisUserQrGrantStore(IConnectionMultiplexer redis, IClock clock)
    {
        _redis = redis;
        _clock = clock;
    }

    public async Task SaveAsync(UserQrGrant grant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);

        var ttl = grant.ExpiresAt - _clock.UtcNow;

        if (ttl > TimeSpan.Zero)
        {
            await _redis.GetDatabase().StringSetAsync(Key(grant.Id), JsonSerializer.Serialize(grant, Json), ttl).ConfigureAwait(false);
        }
    }

    public async Task<UserQrGrant?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var value = await _redis.GetDatabase().StringGetAsync(Key(id)).ConfigureAwait(false);
        return value.HasValue ? JsonSerializer.Deserialize<UserQrGrant>(value.ToString(), Json) : null;
    }

    private static string Key(Guid id) => $"userqr:{id:N}";
}

/// <inheritdoc cref="IUserPrintCodeStore" />
/// <remarks>
/// <c>printcode:{code}</c> → user and <c>printcode:user:{user}</c> → code, both
/// expiring with the code; <c>printcode:fail:{kiosk}</c> counts a kiosk's wrong
/// codes for the window. Lost with Redis, the app shows a new code.
/// </remarks>
public sealed class RedisUserPrintCodeStore : IUserPrintCodeStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IClock _clock;

    public RedisUserPrintCodeStore(IConnectionMultiplexer redis, IClock clock)
    {
        _redis = redis;
        _clock = clock;
    }

    public async Task<(string Code, DateTime ExpiresAt)> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var mine = UserKey(userId);

        // The same code while it lasts: the screen can be opened twice.
        var current = await db.StringGetAsync(mine).ConfigureAwait(false);
        if (current.HasValue)
        {
            var left = await db.KeyTimeToLiveAsync(mine).ConfigureAwait(false);
            if (left is { } ttl && ttl > TimeSpan.FromSeconds(30))
            {
                return (current.ToString(), _clock.UtcNow + ttl);
            }
        }

        // A code nobody holds now; a clash is rare and simply drawn again.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = UserPrintCodes.NewCode();
            if (await db.StringSetAsync(CodeKey(code), userId.ToString("N"), lifetime, When.NotExists).ConfigureAwait(false))
            {
                if (current.HasValue)
                {
                    await db.KeyDeleteAsync(CodeKey(current.ToString())).ConfigureAwait(false);
                }

                await db.StringSetAsync(mine, code, lifetime).ConfigureAwait(false);
                return (code, _clock.UtcNow + lifetime);
            }
        }

        throw new InvalidOperationException("No free print code was found.");
    }

    public async Task<Guid?> TakeAsync(string code, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        // GETDEL: two kiosks typing the same code cannot both have it.
        var value = await db.StringGetDeleteAsync(CodeKey(code)).ConfigureAwait(false);
        if (!value.HasValue || !Guid.TryParseExact(value.ToString(), "N", out var userId))
        {
            return null;
        }

        await db.KeyDeleteAsync(UserKey(userId)).ConfigureAwait(false);
        return userId;
    }

    public async Task<long> CountFailureAsync(Guid kioskId, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = FailKey(kioskId);
        var count = await db.StringIncrementAsync(key).ConfigureAwait(false);

        if (count == 1)
        {
            await db.KeyExpireAsync(key, window).ConfigureAwait(false);
        }

        return count;
    }

    public async Task<long> FailuresAsync(Guid kioskId, CancellationToken cancellationToken = default)
    {
        var value = await _redis.GetDatabase().StringGetAsync(FailKey(kioskId)).ConfigureAwait(false);
        return value.HasValue && long.TryParse(value.ToString(), out var count) ? count : 0;
    }

    private static string CodeKey(string code) => $"printcode:{code}";

    private static string UserKey(Guid userId) => $"printcode:user:{userId:N}";

    private static string FailKey(Guid kioskId) => $"printcode:fail:{kioskId:N}";
}

/// <summary>
/// Every hour: orders paid in the app for later that nobody collected within
/// <c>PreparedPrint.AutoCancelDays</c> are cancelled and their money goes back
/// to the wallet (<c>docs/DECISIONS.md</c> T-144).
/// </summary>
/// <remarks>Safe to run twice: a cancelled job is skipped, and the refund is keyed by the job.</remarks>
public sealed class UncollectedPrintJobsJob
{
    public const string RecurringId = "uncollected-print-jobs";

    private readonly ISender _sender;

    public UncollectedPrintJobsJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 300)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new CancelUncollectedPrintJobsCommand(), cancellationToken);
}
