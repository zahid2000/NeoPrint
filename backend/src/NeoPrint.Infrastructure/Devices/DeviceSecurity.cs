using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.Devices;

/// <summary>
/// Verifies the ECDSA P-256 signatures a kiosk device produces
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// Uses the BCL only. The device's private key never leaves the machine; the
/// backend holds nothing but the public key, so a database breach cannot
/// impersonate a kiosk.
/// </remarks>
public sealed class DeviceSignatureVerifier : IDeviceSignatureVerifier
{
    private readonly ILogger<DeviceSignatureVerifier> _logger;

    public DeviceSignatureVerifier(ILogger<DeviceSignatureVerifier> logger) => _logger = logger;

    /// <inheritdoc />
    public bool IsValidPublicKey(string publicKeySpkiBase64)
    {
        try
        {
            using var ecdsa = ImportKey(publicKeySpkiBase64);

            // Only P-256; accepting other curves would widen the attack surface
            // for no benefit.
            return ecdsa.KeySize == 256;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool Verify(string publicKeySpkiBase64, string nonce, string signatureBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signatureBase64))
        {
            return false;
        }

        try
        {
            using var ecdsa = ImportKey(publicKeySpkiBase64);

            if (ecdsa.KeySize != 256)
            {
                return false;
            }

            var signature = Convert.FromBase64String(signatureBase64);

            // IEEE P1363 (r || s) is what the kiosk's BCL ECDsa.SignData
            // produces by default on both Windows and Linux.
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(nonce),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // Malformed input is a failed verification, not a server error.
            _logger.LogDebug(exception, "A device signature could not be verified.");
            return false;
        }
    }

    private static ECDsa ImportKey(string publicKeySpkiBase64)
    {
        var ecdsa = ECDsa.Create();

        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One-time challenges for device authentication, held in Redis.
/// </summary>
/// <remarks>
/// A challenge is consumed on first use, so a captured signature cannot be
/// replayed. The short lifetime bounds how long one is worth capturing.
/// </remarks>
public sealed class RedisDeviceChallengeStore : IDeviceChallengeStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IConnectionMultiplexer _redis;

    public RedisDeviceChallengeStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<string> IssueAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        await _redis.GetDatabase()
            .StringSetAsync(Key(deviceId, nonce), "1", Lifetime)
            .ConfigureAwait(false);

        return nonce;
    }

    public async Task<bool> RedeemAsync(Guid deviceId, string nonce, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        // Delete returns whether the key was there, which makes this atomic:
        // two callers cannot both redeem the same challenge.
        return await _redis.GetDatabase().KeyDeleteAsync(Key(deviceId, nonce)).ConfigureAwait(false);
    }

    private static RedisKey Key(Guid deviceId, string nonce) => $"device:challenge:{deviceId}:{nonce}";
}

/// <summary>
/// Kiosk tokens that must stop working before they expire, held in Redis.
/// </summary>
public sealed class RedisKioskTokenBlocklist : IKioskTokenBlocklist
{
    private readonly IConnectionMultiplexer _redis;

    public RedisKioskTokenBlocklist(IConnectionMultiplexer redis) => _redis = redis;

    public Task BlockAsync(Guid deviceId, TimeSpan duration, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().StringSetAsync(Key(deviceId), "1", duration);

    public Task<bool> IsBlockedAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyExistsAsync(Key(deviceId));

    private static RedisKey Key(Guid deviceId) => $"device:blocked:{deviceId}";
}
