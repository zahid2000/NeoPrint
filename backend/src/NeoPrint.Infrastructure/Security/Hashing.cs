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
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

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

    /// <summary>
    /// Hashes a short, human-entered secret with PBKDF2 and a random salt.
    /// </summary>
    /// <returns><c>pbkdf2$sha256$iterations$salt$hash</c>, all base64.</returns>
    public static string HashSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, Iterations, Algorithm, KeySize);

        return string.Join('$',
            "pbkdf2", "sha256", Iterations.ToString(),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Verifies a short secret against a hash produced by <see cref="HashSecret"/>.
    /// </summary>
    /// <remarks>
    /// The iteration count is read back from the stored value, so raising it
    /// later does not invalidate existing hashes.
    /// </remarks>
    public static bool VerifySecret(string secret, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256")
        {
            return false;
        }

        if (!int.TryParse(parts[2], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), salt, iterations, Algorithm, expected.Length);

        // Constant time: a timing difference would leak how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
