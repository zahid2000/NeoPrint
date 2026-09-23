namespace NeoPrint.Crypto;

/// <summary>
/// A key encryption key: the key that wraps the per-object data keys.
/// </summary>
/// <remarks>
/// Key encryption keys live in OpenBao under <c>neoprint/backend/crypto</c> as
/// <c>NEOPRINT_KEK__{id}</c>, and <c>NEOPRINT_KEK_CURRENT_ID</c> names the one
/// new objects are wrapped with. Rotating adds a key and moves the pointer;
/// older keys stay until every object has been rewrapped (plan section 2.13).
/// </remarks>
public sealed class KeyEncryptionKey
{
    /// <summary>AES-256 key length in bytes.</summary>
    public const int KeyLengthBytes = 32;

    private readonly byte[] _key;

    /// <param name="id">
    /// The identifier stored in each object header, for example <c>k1</c>.
    /// </param>
    /// <param name="key">A 256-bit key.</param>
    public KeyEncryptionKey(string id, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeyLengthBytes)
        {
            throw new ArgumentException(
                $"A key encryption key must be {KeyLengthBytes} bytes (256 bit), but was {key.Length}.",
                nameof(key));
        }

        // The id travels in the clear inside every object header, so it has to
        // stay within a conservative, filename-safe character set.
        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                throw new ArgumentException(
                    $"A key encryption key id may only contain ASCII letters, digits, '-' and '_', but was '{id}'.",
                    nameof(id));
            }
        }

        Id = id;
        _key = (byte[])key.Clone();
    }

    /// <summary>The identifier written into the header of every object wrapped with this key.</summary>
    public string Id { get; }

    /// <summary>The raw key material.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>
    /// Builds a key from the base64 form the secret store hands out.
    /// </summary>
    /// <exception cref="CryptoFormatException">The value is not valid base64 of the right length.</exception>
    public static KeyEncryptionKey FromBase64(string id, string base64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64);

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new CryptoFormatException(
                $"The key encryption key '{id}' is not valid base64.", exception);
        }

        if (key.Length != KeyLengthBytes)
        {
            throw new CryptoFormatException(
                $"The key encryption key '{id}' decodes to {key.Length} bytes; {KeyLengthBytes} were expected.");
        }

        return new KeyEncryptionKey(id, key);
    }
}
