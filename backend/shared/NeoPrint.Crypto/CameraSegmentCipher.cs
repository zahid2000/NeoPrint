using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace NeoPrint.Crypto;

/// <summary>
/// The kiosk camera's recordings on the kiosk's disk (T-481, T-482): sealed
/// with the backend's camera public key, so the kiosk that wrote a recording
/// cannot read it back.
/// </summary>
/// <remarks>
/// <para>
/// A kiosk is an untrusted device: one that is stolen must not hand over
/// what its camera saw. Each segment gets a fresh 256-bit data key, the
/// content is encrypted with <see cref="EnvelopeEncryptor"/> under that key
/// (chunked AES-256-GCM, every chunk authenticated), and the data key is
/// wrapped with RSA-OAEP-SHA256 for the backend's key. Only the backend holds
/// the private half.
/// </para>
/// <para>
/// Layout: <c>"NPCAM1"</c> | key id length (1 byte) | key id (ASCII) |
/// wrapped key length (2 bytes, big endian) | wrapped key | envelope object.
/// </para>
/// </remarks>
public static class CameraSegmentCipher
{
    /// <summary>What a sealed segment starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "NPCAM1"u8;

    /// <summary>Key ids are short and filename-safe, like the backend's own (<see cref="KeyEncryptionKey"/>).</summary>
    public const int MaxKeyIdLength = 64;

    /// <summary>An RSA-8192 block; anything longer is not a wrapped key.</summary>
    public const int MaxWrappedKeyLength = 1024;

    /// <summary>The id the one-time data key goes by inside the envelope.</summary>
    private const string DataKeyId = "camera";

    /// <summary>Seals <paramref name="plaintext"/> for the holder of the private half of <paramref name="publicKey"/>.</summary>
    /// <param name="keyId">The backend's name for the key pair, written in the clear so it knows which private key opens it.</param>
    public static async Task EncryptAsync(
        Stream plaintext, Stream ciphertext, string keyId, RSA publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(publicKey);

        if (!IsKeyId(keyId))
        {
            throw new ArgumentException($"A camera key id is 1-{MaxKeyIdLength} ASCII letters, digits, '-' or '_'.", nameof(keyId));
        }

        var dataKey = RandomNumberGenerator.GetBytes(KeyEncryptionKey.KeyLengthBytes);
        try
        {
            var wrapped = publicKey.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
            var id = Encoding.ASCII.GetBytes(keyId);

            var header = new byte[Magic.Length + 1 + id.Length + 2 + wrapped.Length];
            var at = 0;
            Magic.CopyTo(header);
            at += Magic.Length;
            header[at++] = (byte)id.Length;
            id.CopyTo(header, at);
            at += id.Length;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(at, 2), (ushort)wrapped.Length);
            at += 2;
            wrapped.CopyTo(header, at);

            await ciphertext.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            var envelope = new EnvelopeEncryptor(new KeyRing([new KeyEncryptionKey(DataKeyId, dataKey)], DataKeyId));
            await envelope.EncryptAsync(plaintext, ciphertext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Reads which key a sealed segment was made for, leaving the stream after the header.</summary>
    /// <exception cref="CryptoFormatException">It is not a sealed segment.</exception>
    public static async Task<SealedSegmentHeader> ReadHeaderAsync(Stream ciphertext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        var magic = new byte[Magic.Length + 1];
        await ReadExactlyAsync(ciphertext, magic, cancellationToken).ConfigureAwait(false);

        if (!magic.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptoFormatException("This is not a sealed camera segment.");
        }

        var idLength = magic[Magic.Length];
        if (idLength is 0 or > MaxKeyIdLength)
        {
            throw new CryptoFormatException("The camera segment names no valid key.");
        }

        var id = new byte[idLength + 2];
        await ReadExactlyAsync(ciphertext, id, cancellationToken).ConfigureAwait(false);

        var keyId = Encoding.ASCII.GetString(id, 0, idLength);
        if (!IsKeyId(keyId))
        {
            throw new CryptoFormatException("The camera segment names no valid key.");
        }

        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(id.AsSpan(idLength, 2));
        if (wrappedLength is 0 or > MaxWrappedKeyLength)
        {
            throw new CryptoFormatException("The camera segment's key is malformed.");
        }

        var wrapped = new byte[wrappedLength];
        await ReadExactlyAsync(ciphertext, wrapped, cancellationToken).ConfigureAwait(false);

        return new SealedSegmentHeader(keyId, wrapped);
    }

    /// <summary>Opens a sealed segment with the backend's private key.</summary>
    /// <param name="privateKey">Finds the private key by its id; null when the backend has no such key.</param>
    /// <exception cref="CryptoFormatException">It is not a sealed segment, or it is cut short.</exception>
    /// <exception cref="UnknownKeyException">The key it was sealed for is not known.</exception>
    /// <exception cref="CryptoAuthenticationException">It was changed, or sealed for another key.</exception>
    public static async Task DecryptAsync(
        Stream ciphertext, Stream plaintext, Func<string, RSA?> privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(privateKey);

        var header = await ReadHeaderAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        var rsa = privateKey(header.KeyId) ?? throw new UnknownKeyException(header.KeyId);

        byte[] dataKey;
        try
        {
            dataKey = rsa.Decrypt(header.WrappedKey, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException exception)
        {
            throw new CryptoAuthenticationException("The camera segment's key does not open with this private key.", exception);
        }

        try
        {
            if (dataKey.Length != KeyEncryptionKey.KeyLengthBytes)
            {
                throw new CryptoAuthenticationException("The camera segment's key has the wrong length.");
            }

            var envelope = new EnvelopeEncryptor(new KeyRing([new KeyEncryptionKey(DataKeyId, dataKey)], DataKeyId));
            await envelope.DecryptAsync(ciphertext, plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>1-64 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsKeyId([NotNullWhen(true)] string? keyId)
        => !string.IsNullOrEmpty(keyId)
            && keyId.Length <= MaxKeyIdLength
            && keyId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new CryptoFormatException("The camera segment is cut short.", exception);
        }
    }
}

/// <summary>The clear part of a sealed camera segment.</summary>
/// <param name="KeyId">The backend's camera key it was sealed for.</param>
/// <param name="WrappedKey">The segment's data key, wrapped with RSA-OAEP-SHA256.</param>
public sealed record SealedSegmentHeader(string KeyId, byte[] WrappedKey);
