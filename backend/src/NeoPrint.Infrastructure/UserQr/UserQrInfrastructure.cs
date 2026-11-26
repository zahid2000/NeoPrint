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
