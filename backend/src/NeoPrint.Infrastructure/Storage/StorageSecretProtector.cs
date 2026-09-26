using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;

namespace NeoPrint.Infrastructure.Storage;

/// <summary>
/// Encrypts the credentials a storage provider is configured with.
/// </summary>
/// <remarks>
/// <para>
/// The access key and secret live in <c>storage_providers</c>, which is an
/// ordinary Mongo collection: a database dump, a backup or a stray read would
/// otherwise hand over the object store and everything in it. They are sealed
/// with <c>NEOPRINT_STORAGE_SECRETS_KEY</c>, which comes from OpenBao and is
/// never in the database (plan section 2.13).
/// </para>
/// <para>
/// AES-256-GCM with a fresh nonce per value, and the provider id as
/// associated data, so a ciphertext copied from one provider's document to
/// another's fails to open rather than silently granting the first provider's
/// credentials to the second.
/// </para>
/// </remarks>
public sealed class StorageSecretProtector
{
    private const string KeyName = "NEOPRINT_STORAGE_SECRETS_KEY";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly byte[] _key;

    public StorageSecretProtector(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[KeyName];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"'{KeyName}' is not configured; storage provider credentials cannot be read or written (docs/OPENBAO.md).");
        }

        _key = Convert.FromBase64String(configured);

        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                $"'{KeyName}' has to be a base64-encoded 256-bit key; it decoded to {_key.Length} bytes.");
        }
    }

    /// <summary>Seals a credential for storage.</summary>
    /// <param name="providerId">
    /// Bound into the ciphertext, so it cannot be moved to another provider's
    /// document.
    /// </param>
    public string Protect(string plaintext, Guid providerId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var content = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[content.Length];
        var tag = new byte[TagLength];

        using (var cipher = new AesGcm(_key, TagLength))
        {
            cipher.Encrypt(nonce, content, ciphertext, tag, Aad(providerId));
        }

        CryptographicOperations.ZeroMemory(content);

        // nonce | tag | ciphertext, base64. One field, so the document shape
        // does not have to change when the format does.
        var packed = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceLength);
        ciphertext.CopyTo(packed, NonceLength + TagLength);

        return Convert.ToBase64String(packed);
    }

    /// <summary>Opens a credential that <see cref="Protect"/> sealed.</summary>
    /// <exception cref="InvalidOperationException">
    /// The value was tampered with, belongs to another provider, or the key
    /// has changed.
    /// </exception>
    public string Unprotect(string protectedValue, Guid providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);

        byte[] packed;

        try
        {
            packed = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"The stored credential for provider {providerId} is not valid base64.", exception);
        }

        if (packed.Length < NonceLength + TagLength)
        {
            throw new InvalidOperationException(
                $"The stored credential for provider {providerId} is too short to be a sealed value.");
        }

        var nonce = packed.AsSpan(0, NonceLength);
        var tag = packed.AsSpan(NonceLength, TagLength);
        var ciphertext = packed.AsSpan(NonceLength + TagLength);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var cipher = new AesGcm(_key, TagLength);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, Aad(providerId));

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            // Never say which of the three it was: the difference is useful to
            // somebody probing, and to nobody operating the system.
            throw new InvalidOperationException(
                $"The stored credential for provider {providerId} failed authentication. "
                + "It was modified, copied from another provider, or the storage secrets key has changed.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Aad(Guid providerId)
        => Encoding.UTF8.GetBytes($"neoprint:storage-provider:{providerId:N}");
}
