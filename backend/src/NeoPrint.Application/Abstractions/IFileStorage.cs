namespace NeoPrint.Application.Abstractions;

/// <summary>Which bucket an object belongs in (plan section 2.10).</summary>
public enum StorageBucket
{
    /// <summary>User files, scans, layouts and their versions.</summary>
    Files = 0,

    Receipts = 1,

    /// <summary>Preview PDFs and conversion output; cleaned up after 24 hours.</summary>
    Temp = 2,
}

/// <summary>
/// Where an object ended up, recorded on the document that owns it.
/// </summary>
/// <remarks>
/// The provider is part of the answer, not just the key. Reads follow the
/// provider the object was written to, so changing the primary leaves older
/// objects reachable (plan section 2.10).
/// </remarks>
/// <param name="ProviderId">The provider the object was written to.</param>
/// <param name="Key">The object key inside the bucket.</param>
/// <param name="Sha256">
/// Hash of the <em>encrypted</em> bytes, hex-encoded. What the store holds is
/// what this covers, so a sync can verify a copy without decrypting it.
/// </param>
/// <param name="EncryptedLength">Bytes actually stored, after encryption.</param>
/// <param name="PlaintextLength">Bytes the caller supplied.</param>
/// <param name="KekId">
/// The key encryption key the object's data key is wrapped with, so
/// <c>KeyRewrapJob</c> can find what a rotation still has to cover.
/// </param>
public sealed record StoredObject(
    Guid ProviderId,
    string Key,
    string Sha256,
    long EncryptedLength,
    long PlaintextLength,
    string KekId);

/// <summary>
/// Reading and writing the objects NeoPrint stores.
/// </summary>
/// <remarks>
/// <para>
/// Everything written through this interface is encrypted before it leaves the
/// process and decrypted after it comes back (plan section 2.13). Nothing
/// hands out a plaintext byte to the object store, and nothing hands out a
/// pre-signed URL: an object fetched straight from MinIO is ciphertext, which
/// is the property <c>StorageEncryptionTests</c> exists to keep.
/// </para>
/// <para>
/// Streams throughout. A 50 MB upload must not be held in memory, and the
/// encryptor works in chunks for the same reason.
/// </para>
/// </remarks>
public interface IFileStorage
{
    /// <summary>
    /// Encrypts <paramref name="content"/> and writes it to the current
    /// primary provider.
    /// </summary>
    /// <param name="key">
    /// The object key. Callers build it from the owning aggregate's id, so an
    /// object is traceable back to what owns it.
    /// </param>
    Task<StoredObject> WriteAsync(
        StorageBucket bucket,
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and decrypts an object from the provider it was written to.
    /// </summary>
    /// <exception cref="StorageObjectNotFoundException">No such object.</exception>
    Task ReadAsync(
        StoredObject stored,
        StorageBucket bucket,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an object as a stream, for handing to an HTTP response without
    /// buffering the whole file.
    /// </summary>
    Task<Stream> OpenReadAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an object from <em>every</em> provider that could hold it.
    /// </summary>
    /// <remarks>
    /// Primary, secondary and any migration source: a file the user deleted
    /// must not survive on the provider a sync left it on (plan section 2.10).
    /// A provider that does not have the object is not an error.
    /// </remarks>
    Task DeleteAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the object is present on the provider it was written to.
    /// </summary>
    Task<bool> ExistsAsync(
        StoredObject stored,
        StorageBucket bucket,
        CancellationToken cancellationToken = default);
}

/// <summary>The object is not in the store the document points at.</summary>
public sealed class StorageObjectNotFoundException : Exception
{
    public StorageObjectNotFoundException(string key, Guid providerId)
        : base($"Object '{key}' was not found on storage provider {providerId}.")
    {
        Key = key;
        ProviderId = providerId;
    }

    public string Key { get; }

    public Guid ProviderId { get; }
}
