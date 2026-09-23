using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeoPrint.Crypto;

/// <summary>
/// Envelope encryption for every object NeoPrint stores: user files, scans,
/// layouts, file versions, preview PDFs, receipts, temporary files and the
/// kiosk's local spool (plan section 2.13).
/// </summary>
/// <remarks>
/// <para>
/// Each object gets a fresh 256-bit data key. The content is encrypted with
/// AES-256-GCM in chunks, each chunk carrying its own nonce and authentication
/// tag, so arbitrarily large files stream in constant memory. The data key is
/// then wrapped with the active key encryption key and stored in the object
/// header.
/// </para>
/// <para>
/// Every chunk is authenticated against the object id, its own index and a
/// final-chunk flag. Reordering, duplicating, dropping or truncating chunks
/// therefore fails authentication rather than yielding plausible plaintext.
/// </para>
/// <para>
/// Rotation only rewraps the data key: <see cref="RewrapAsync"/> rewrites the
/// header and copies the ciphertext through untouched, which is what makes
/// <c>KeyRewrapJob</c> cheap even for large objects.
/// </para>
/// </remarks>
public sealed class EnvelopeEncryptor
{
    /// <summary>Default plaintext bytes per chunk (1 MB, plan section 2.13).</summary>
    public const int DefaultChunkSize = 1024 * 1024;

    /// <summary>Upper bound accepted when reading a header, to cap buffer allocation.</summary>
    public const int MaxChunkSize = 64 * 1024 * 1024;

    private const int NonceLength = EncryptedObjectHeader.NonceLength;
    private const int TagLength = EncryptedObjectHeader.TagLength;

    /// <summary>Per-chunk frame overhead: nonce, final flag, length and tag.</summary>
    private const int ChunkFrameOverhead = NonceLength + 1 + 4 + TagLength;

    private readonly IKeyEncryptionKeyProvider _keys;
    private readonly int _chunkSize;

    public EnvelopeEncryptor(IKeyEncryptionKeyProvider keys, int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkSize, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxChunkSize);

        _keys = keys;
        _chunkSize = chunkSize;
    }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into <paramref name="ciphertext"/>
    /// with the currently active key encryption key.
    /// </summary>
    /// <returns>
    /// The header that was written, for recording <c>files.Encryption</c>.
    /// </returns>
    public async Task<EncryptedObjectHeader> EncryptAsync(
        Stream plaintext, Stream ciphertext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);

        var kek = _keys.Current;

        var dataKey = RandomNumberGenerator.GetBytes(KeyEncryptionKey.KeyLengthBytes);
        try
        {
            var objectId = RandomNumberGenerator.GetBytes(EncryptedObjectHeader.ObjectIdLength);
            var plaintextLength = TryGetLength(plaintext);

            var dataKeyNonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrappedDataKey = new byte[KeyEncryptionKey.KeyLengthBytes];
            var dataKeyTag = new byte[TagLength];

            var header = new EncryptedObjectHeader(
                EncryptedObjectHeader.CurrentFormatVersion,
                objectId,
                _chunkSize,
                plaintextLength,
                kek.Id,
                dataKeyNonce,
                wrappedDataKey,
                dataKeyTag);

            using (var kekCipher = new AesGcm(kek.Key, TagLength))
            {
                kekCipher.Encrypt(dataKeyNonce, dataKey, wrappedDataKey, dataKeyTag, header.BuildDataKeyAad());
            }

            await ciphertext.WriteAsync(header.ToBytes(), cancellationToken).ConfigureAwait(false);
            await WriteChunksAsync(plaintext, ciphertext, header, dataKey, cancellationToken).ConfigureAwait(false);

            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// Decrypts <paramref name="ciphertext"/> into <paramref name="plaintext"/>.
    /// </summary>
    /// <exception cref="CryptoFormatException">The object is malformed or truncated.</exception>
    /// <exception cref="UnknownKeyException">The key encryption key is not available.</exception>
    /// <exception cref="CryptoAuthenticationException">Authentication failed.</exception>
    public async Task DecryptAsync(
        Stream ciphertext, Stream plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);

        var header = await EncryptedObjectHeader.ReadAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        var dataKey = UnwrapDataKey(header);

        try
        {
            await ReadChunksAsync(ciphertext, plaintext, header, dataKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// Rewraps an object's data key with the currently active key encryption
    /// key, copying the encrypted content through unchanged.
    /// </summary>
    /// <remarks>
    /// This is the whole of a key rotation for an existing object: the content
    /// is never re-encrypted, so the cost does not depend on the object size
    /// (plan section 2.13).
    /// </remarks>
    /// <returns>The new header, or <see langword="null"/> when the object was already current.</returns>
    public async Task<EncryptedObjectHeader?> RewrapAsync(
        Stream source, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        var header = await EncryptedObjectHeader.ReadAsync(source, cancellationToken).ConfigureAwait(false);
        var target = _keys.Current;

        if (string.Equals(header.KekId, target.Id, StringComparison.Ordinal))
        {
            // Already wrapped with the active key. Copy it through so callers can
            // use one code path, and report that nothing changed.
            await destination.WriteAsync(header.ToBytes(), cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var dataKey = UnwrapDataKey(header);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var wrapped = new byte[KeyEncryptionKey.KeyLengthBytes];
            var tag = new byte[TagLength];

            // The additional data covers only the immutable fields, so the same
            // value authenticates the new wrapping.
            using (var kekCipher = new AesGcm(target.Key, TagLength))
            {
                kekCipher.Encrypt(nonce, dataKey, wrapped, tag, header.BuildDataKeyAad());
            }

            var rewrapped = header.WithWrappedKey(target.Id, nonce, wrapped, tag);

            await destination.WriteAsync(rewrapped.ToBytes(), cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            return rewrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>
    /// Ciphertext length for a given plaintext length, without encrypting
    /// anything. Used to size uploads and quota checks.
    /// </summary>
    public long GetCiphertextLength(long plaintextLength, string kekId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);

        var header = new EncryptedObjectHeader(
            EncryptedObjectHeader.CurrentFormatVersion,
            new byte[EncryptedObjectHeader.ObjectIdLength],
            _chunkSize,
            plaintextLength,
            kekId,
            new byte[NonceLength],
            new byte[KeyEncryptionKey.KeyLengthBytes],
            new byte[TagLength]);

        // Even an empty object is written as a single final chunk, so that the
        // final-chunk marker is always present.
        var chunkCount = plaintextLength == 0 ? 1 : (plaintextLength + _chunkSize - 1) / _chunkSize;

        return header.Length + plaintextLength + (chunkCount * ChunkFrameOverhead);
    }

    private byte[] UnwrapDataKey(EncryptedObjectHeader header)
    {
        if (!_keys.TryGet(header.KekId, out var kek))
        {
            throw new UnknownKeyException(header.KekId);
        }

        var dataKey = new byte[KeyEncryptionKey.KeyLengthBytes];
        try
        {
            using var kekCipher = new AesGcm(kek.Key, TagLength);
            kekCipher.Decrypt(
                header.DataKeyNonce, header.WrappedDataKey, header.DataKeyTag, dataKey, header.BuildDataKeyAad());
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new CryptoAuthenticationException(
                "The wrapped data key failed authentication. The header was modified, or it was wrapped with a different key.",
                exception);
        }

        return dataKey;
    }

    private async Task WriteChunksAsync(
        Stream plaintext,
        Stream ciphertext,
        EncryptedObjectHeader header,
        byte[] dataKey,
        CancellationToken cancellationToken)
    {
        using var cipher = new AesGcm(dataKey, TagLength);

        var currentBuffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
        var lookaheadBuffer = ArrayPool<byte>.Shared.Rent(_chunkSize);
        var frame = ArrayPool<byte>.Shared.Rent(_chunkSize + ChunkFrameOverhead);

        try
        {
            var currentLength = await FillAsync(plaintext, currentBuffer, _chunkSize, cancellationToken)
                .ConfigureAwait(false);
            long index = 0;

            while (true)
            {
                // Only a full chunk can be followed by another one, so a short
                // read already proves the current chunk is the last.
                var lookaheadLength = currentLength == _chunkSize
                    ? await FillAsync(plaintext, lookaheadBuffer, _chunkSize, cancellationToken).ConfigureAwait(false)
                    : 0;
                var isFinal = lookaheadLength == 0;

                var written = EncryptChunk(
                    cipher, header, index, isFinal, currentBuffer.AsSpan(0, currentLength), frame);
                await ciphertext.WriteAsync(frame.AsMemory(0, written), cancellationToken).ConfigureAwait(false);

                if (isFinal)
                {
                    break;
                }

                (currentBuffer, lookaheadBuffer) = (lookaheadBuffer, currentBuffer);
                currentLength = lookaheadLength;
                index++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(currentBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(lookaheadBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(frame, clearArray: true);
        }
    }

    private static int EncryptChunk(
        AesGcm cipher,
        EncryptedObjectHeader header,
        long index,
        bool isFinal,
        ReadOnlySpan<byte> plaintext,
        Span<byte> frame)
    {
        var nonce = frame[..NonceLength];
        RandomNumberGenerator.Fill(nonce);

        frame[NonceLength] = isFinal ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(NonceLength + 1, 4), plaintext.Length);

        var payloadStart = NonceLength + 1 + 4;
        var payload = frame.Slice(payloadStart, plaintext.Length);
        var tag = frame.Slice(payloadStart + plaintext.Length, TagLength);

        cipher.Encrypt(nonce, plaintext, payload, tag, header.BuildChunkAad(index, isFinal));

        return payloadStart + plaintext.Length + TagLength;
    }

    private static async Task ReadChunksAsync(
        Stream ciphertext,
        Stream plaintext,
        EncryptedObjectHeader header,
        byte[] dataKey,
        CancellationToken cancellationToken)
    {
        using var cipher = new AesGcm(dataKey, TagLength);

        var frameHeader = new byte[NonceLength + 1 + 4];
        // The payload buffer holds a full chunk plus its authentication tag,
        // which are read in one go.
        var payload = ArrayPool<byte>.Shared.Rent(header.ChunkSize + TagLength);
        var output = ArrayPool<byte>.Shared.Rent(header.ChunkSize);

        try
        {
            long index = 0;
            long total = 0;

            while (true)
            {
                await EncryptedObjectHeader
                    .ReadExactlyAsync(ciphertext, frameHeader, $"frame header of chunk {index}", cancellationToken)
                    .ConfigureAwait(false);

                var flag = frameHeader[NonceLength];
                if (flag is not (0 or 1))
                {
                    throw new CryptoFormatException(
                        $"Chunk {index} carries an invalid final-chunk flag ({flag}).");
                }

                var isFinal = flag == 1;
                var length = BinaryPrimitives.ReadInt32LittleEndian(frameHeader.AsSpan(NonceLength + 1, 4));

                if (length < 0 || length > header.ChunkSize)
                {
                    throw new CryptoFormatException(
                        $"Chunk {index} declares a length of {length} bytes, which does not fit the declared chunk size of {header.ChunkSize}.");
                }

                await EncryptedObjectHeader
                    .ReadExactlyAsync(ciphertext, payload.AsMemory(0, length + TagLength), $"chunk {index}", cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    cipher.Decrypt(
                        frameHeader.AsSpan(0, NonceLength),
                        payload.AsSpan(0, length),
                        payload.AsSpan(length, TagLength),
                        output.AsSpan(0, length),
                        header.BuildChunkAad(index, isFinal));
                }
                catch (AuthenticationTagMismatchException exception)
                {
                    // A mismatch here means the content was modified, or that
                    // chunks were reordered, duplicated or dropped: the index and
                    // the final flag are part of what is authenticated.
                    throw new CryptoAuthenticationException(
                        $"Chunk {index} failed authentication. The object was modified, reordered or truncated, or the wrong key was used.",
                        exception);
                }

                await plaintext.WriteAsync(output.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                total += length;

                if (isFinal)
                {
                    break;
                }

                index++;
            }

            if (header.PlaintextLength >= 0 && total != header.PlaintextLength)
            {
                throw new CryptoAuthenticationException(
                    $"The object decrypted to {total} bytes but its header declares {header.PlaintextLength}.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload, clearArray: true);
            ArrayPool<byte>.Shared.Return(output, clearArray: true);
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends.</summary>
    private static async Task<int> FillAsync(
        Stream source, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var got = await source.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken)
                .ConfigureAwait(false);
            if (got == 0)
            {
                break;
            }
            read += got;
        }
        return read;
    }

    private static long TryGetLength(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return -1;
        }

        try
        {
            return stream.Length - stream.Position;
        }
        catch (NotSupportedException)
        {
            return -1;
        }
    }
}
