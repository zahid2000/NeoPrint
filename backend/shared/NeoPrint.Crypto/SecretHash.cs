using System.Security.Cryptography;
using System.Text;

namespace NeoPrint.Crypto;

/// <summary>
/// A slow, salted hash for short secrets a person types: the kiosk admin PIN,
/// a pairing code, a confirmation code.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the backend alone because the kiosk checks its admin
/// PIN against the hash the backend sends in its configuration (plan section
/// 7.11): one definition of the format, so the two cannot drift apart.
/// </para>
/// <para>
/// Short and from a small alphabet, these secrets need PBKDF2 with a salt so a
/// leaked hash cannot simply be looked up. The iteration count is stored with
/// the hash, so raising it later does not invalidate existing ones.
/// </para>
/// </remarks>
public static class SecretHash
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <returns><c>pbkdf2$sha256$iterations$salt$hash</c>, salt and hash in base64.</returns>
    public static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, Algorithm, KeySize);

        return string.Join('$', "pbkdf2", "sha256", Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>Whether <paramref name="secret"/> is the one <paramref name="storedHash"/> was made from.</summary>
    public static bool Verify(string secret, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');

        if (parts.Length != 5 || parts[0] != "pbkdf2" || parts[1] != "sha256"
            || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations <= 0)
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

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, Algorithm, expected.Length);

        // Constant time: a timing difference would leak how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
