using System.Buffers.Binary;
using System.Text;

namespace NeoPrint.Crypto;

/// <summary>
/// The clear-text header that precedes the encrypted chunks of every stored
/// object (plan section 2.13).
/// </summary>
/// <remarks>
/// <para>Layout, all integers little-endian:</para>
/// <code>
///   offset  size  field
///        0     5  magic "NPENC"
///        5     1  format version (currently 1)
///        6    16  object id
///       22     4  chunk size in bytes
///       26     8  plaintext length, or -1 when it was not known up front
///       34     1  length of the key encryption key id
///       35     n  key encryption key id (ASCII)
///     35+n    12  nonce used to wrap the data key
///     47+n    32  wrapped data key
///     79+n    16  authentication tag of the wrapped data key
/// </code>
/// <para>
/// <see cref="ObjectId"/>, <see cref="ChunkSize"/> and
/// <see cref="PlaintextLength"/> are fixed for the life of an object: the
/// chunks are authenticated against the object id, and the data key wrapping is
/// authenticated against all three. <see cref="KekId"/> and the wrapping fields
/// are the only ones a rewrap changes, which is what lets a key rotation
/// rewrite nothing but this header.
/// </para>
/// </remarks>
public sealed class EncryptedObjectHeader
{
    /// <summary>File magic: identifies a NeoPrint encrypted object.</summary>
    public static readonly byte[] Magic = "NPENC"u8.ToArray();

    /// <summary>The only format version written today.</summary>
    public const byte CurrentFormatVersion = 1;

    internal const int ObjectIdLength = 16;
    internal const int NonceLength = 12;
    internal const int TagLength = 16;
    internal const int WrappedKeyLength = KeyEncryptionKey.KeyLengthBytes;

    private const int FixedPrefixLength =
        5 /* magic */ + 1 /* version */ + ObjectIdLength + 4 /* chunk size */ + 8 /* plaintext length */ + 1 /* kek id length */;

    /// <summary>Largest header we will read, which bounds the key id length.</summary>
    internal const int MaxKekIdLength = 64;

    public EncryptedObjectHeader(
        byte formatVersion,
        byte[] objectId,
        int chunkSize,
        long plaintextLength,
        string kekId,
        byte[] dataKeyNonce,
        byte[] wrappedDataKey,
        byte[] dataKeyTag)
    {
        FormatVersion = formatVersion;
        ObjectId = objectId;
        ChunkSize = chunkSize;
        PlaintextLength = plaintextLength;
        KekId = kekId;
        DataKeyNonce = dataKeyNonce;
        WrappedDataKey = wrappedDataKey;
        DataKeyTag = dataKeyTag;
    }

    public byte FormatVersion { get; }

    /// <summary>Random per-object identifier; every chunk is authenticated against it.</summary>
    public byte[] ObjectId { get; }

    /// <summary>Plaintext bytes per chunk.</summary>
    public int ChunkSize { get; }

    /// <summary>Total plaintext length, or -1 when the source stream could not be measured.</summary>
    public long PlaintextLength { get; }

    /// <summary>Which key encryption key the data key is wrapped with.</summary>
    public string KekId { get; }

    public byte[] DataKeyNonce { get; }

    public byte[] WrappedDataKey { get; }

    public byte[] DataKeyTag { get; }

    /// <summary>The algorithm recorded in <c>files.Encryption</c>.</summary>
    public static string AlgorithmName => "AES-256-GCM";

    /// <summary>Serialized length of this header.</summary>
    public int Length =>
        FixedPrefixLength + Encoding.ASCII.GetByteCount(KekId) + NonceLength + WrappedKeyLength + TagLength;

    /// <summary>
    /// Additional authenticated data for the data key wrapping. Covers exactly
    /// the fields a rewrap must not change, so that a rewrap can reuse it.
    /// </summary>
    internal byte[] BuildDataKeyAad()
    {
        const string label = "NPENC-DEK";
        var aad = new byte[label.Length + 1 + ObjectIdLength + 4 + 8];

        var offset = Encoding.ASCII.GetBytes(label, aad);
        aad[offset++] = FormatVersion;
        ObjectId.CopyTo(aad, offset);
        offset += ObjectIdLength;
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(offset), ChunkSize);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(offset), PlaintextLength);

        return aad;
    }

    /// <summary>
    /// Additional authenticated data for one chunk. Binding the index and the
    /// final-chunk flag is what makes reordering, duplicating, dropping and
    /// truncating chunks detectable (plan section 11).
    /// </summary>
    internal byte[] BuildChunkAad(long chunkIndex, bool isFinal)
    {
        const string label = "NPENC-CHUNK";
        var aad = new byte[label.Length + ObjectIdLength + 8 + 1];

        var offset = Encoding.ASCII.GetBytes(label, aad);
        ObjectId.CopyTo(aad, offset);
        offset += ObjectIdLength;
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(offset), chunkIndex);
        offset += 8;
        aad[offset] = isFinal ? (byte)1 : (byte)0;

        return aad;
    }

    /// <summary>Writes this header in wire format.</summary>
    public byte[] ToBytes()
    {
        var kekIdBytes = Encoding.ASCII.GetBytes(KekId);
        var buffer = new byte[Length];
        var offset = 0;

        Magic.CopyTo(buffer, offset);
        offset += Magic.Length;
        buffer[offset++] = FormatVersion;
        ObjectId.CopyTo(buffer, offset);
        offset += ObjectIdLength;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), ChunkSize);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), PlaintextLength);
        offset += 8;
        buffer[offset++] = (byte)kekIdBytes.Length;
        kekIdBytes.CopyTo(buffer, offset);
        offset += kekIdBytes.Length;
        DataKeyNonce.CopyTo(buffer, offset);
        offset += NonceLength;
        WrappedDataKey.CopyTo(buffer, offset);
        offset += WrappedKeyLength;
        DataKeyTag.CopyTo(buffer, offset);

        return buffer;
    }

    /// <summary>
    /// Reads a header from the start of a stream, leaving the stream positioned
    /// at the first chunk.
    /// </summary>
    /// <exception cref="CryptoFormatException">The data is not a well-formed header.</exception>
    public static async Task<EncryptedObjectHeader> ReadAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var prefix = new byte[FixedPrefixLength];
        await ReadExactlyAsync(source, prefix, "header", cancellationToken).ConfigureAwait(false);

        if (!prefix.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptoFormatException(
                "The data does not start with the NeoPrint encrypted object magic; it is not an encrypted object.");
        }

        var offset = Magic.Length;
        var formatVersion = prefix[offset++];
        if (formatVersion != CurrentFormatVersion)
        {
            throw new CryptoFormatException(
                $"Encrypted object format version {formatVersion} is not supported; this build writes and reads version {CurrentFormatVersion}.");
        }

        var objectId = prefix.AsSpan(offset, ObjectIdLength).ToArray();
        offset += ObjectIdLength;
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(offset));
        offset += 4;
        var plaintextLength = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(offset));
        offset += 8;
        var kekIdLength = prefix[offset];

        if (chunkSize is <= 0 or > EnvelopeEncryptor.MaxChunkSize)
        {
            throw new CryptoFormatException(
                $"The header declares a chunk size of {chunkSize} bytes, which is outside the supported range.");
        }

        if (kekIdLength is 0 or > MaxKekIdLength)
        {
            throw new CryptoFormatException(
                $"The header declares a key encryption key id of {kekIdLength} bytes, which is outside the supported range.");
        }

        var rest = new byte[kekIdLength + NonceLength + WrappedKeyLength + TagLength];
        await ReadExactlyAsync(source, rest, "header", cancellationToken).ConfigureAwait(false);

        var restOffset = 0;
        var kekId = Encoding.ASCII.GetString(rest, restOffset, kekIdLength);
        restOffset += kekIdLength;
        var nonce = rest.AsSpan(restOffset, NonceLength).ToArray();
        restOffset += NonceLength;
        var wrappedKey = rest.AsSpan(restOffset, WrappedKeyLength).ToArray();
        restOffset += WrappedKeyLength;
        var tag = rest.AsSpan(restOffset, TagLength).ToArray();

        return new EncryptedObjectHeader(
            formatVersion, objectId, chunkSize, plaintextLength, kekId, nonce, wrappedKey, tag);
    }

    /// <summary>Returns a copy of this header wrapped with a different key.</summary>
    internal EncryptedObjectHeader WithWrappedKey(string kekId, byte[] nonce, byte[] wrappedKey, byte[] tag)
        => new(FormatVersion, ObjectId, ChunkSize, PlaintextLength, kekId, nonce, wrappedKey, tag);

    internal static async Task ReadExactlyAsync(
        Stream source, Memory<byte> buffer, string what, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await source.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (got == 0)
            {
                throw new CryptoFormatException(
                    $"The encrypted object ends inside the {what}: {buffer.Length} bytes were expected, {read} were available.");
            }
            read += got;
        }
    }
}
