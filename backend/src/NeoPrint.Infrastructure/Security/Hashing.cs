using System.Security.Cryptography;
using System.Text;

namespace NeoPrint.Infrastructure.Security;

/// <summary>
/// Hashing for the secrets that are not user passwords.
/// </summary>
/// <remarks>
/// Two different treatments, because the inputs differ:
/// <list type="bullet">
/// <item>
/// High-entropy values we generated ourselves - enrollment tokens, refresh
/// tokens - are 256-bit random. Guessing one is infeasible, so a plain SHA-256
/// is enough and stays fast enough to look up by hash on every request.
/// </item>
/// <item>
/// Low-entropy values a person types - the kiosk admin PIN, a pairing code, an
/// email confirmation code - are short and drawn from a small alphabet. Those
/// need a slow hash with a salt, so a leaked database cannot be brute-forced.
/// </item>
/// </list>
/// </remarks>
public static class Hashing
{
    /// <summary>
    /// Hashes a value we generated with full entropy, for lookup by hash.
    /// </summary>
    /// <remarks>
    /// Deterministic on purpose: a token is found by hashing what the caller
    /// presented and matching it against the stored hash.
    /// </remarks>
    public static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    /// <summary>Hashes a short, human-entered secret with PBKDF2 and a random salt (<see cref="NeoPrint.Crypto.SecretHash"/>).</summary>
    /// <returns><c>pbkdf2$sha256$iterations$salt$hash</c>, all base64.</returns>
    public static string HashSecret(string secret) => NeoPrint.Crypto.SecretHash.Hash(secret);

    /// <summary>Verifies a short secret against a hash produced by <see cref="HashSecret"/>.</summary>
    public static bool VerifySecret(string secret, string? storedHash) => NeoPrint.Crypto.SecretHash.Verify(secret, storedHash);
}
