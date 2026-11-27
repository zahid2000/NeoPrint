using System.Collections.Concurrent;
using System.Security.Cryptography;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Crypto;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// <c>camera_keys</c>: the kiosk camera's key pair (T-482). RSA-3072; the
/// private key is kept as an envelope object under the backend's key
/// encryption key, like every file the backend stores (plan section 2.13).
/// </summary>
/// <remarks>
/// A singleton, kept in memory once read: the pair never changes, and every kiosk's
/// configuration asks for the public half.
/// </remarks>
public sealed class CameraKeyStore : ICameraKeys
{
    private const int KeySizeBits = 3072;

    private readonly SemaphoreSlim _createGate = new(1, 1);
    private CameraPublicKey? _current;
    private readonly ConcurrentDictionary<string, byte[]> _privateKeys = new(StringComparer.Ordinal);

    private readonly MongoContext _context;
    private readonly EnvelopeEncryptor _encryptor;
    private readonly IClock _clock;

    public CameraKeyStore(MongoContext context, EnvelopeEncryptor encryptor, IClock clock)
    {
        _context = context;
        _encryptor = encryptor;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<CameraPublicKey> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _current) is { } cached)
        {
            return cached;
        }

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = await OldestAsync(cancellationToken).ConfigureAwait(false)
                ?? await CreateAsync(cancellationToken).ConfigureAwait(false);

            var current = new CameraPublicKey(key.KeyId, Convert.ToBase64String(key.PublicKey));
            Volatile.Write(ref _current, current);
            return current;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RSA?> OpenPrivateKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (!CameraSegmentCipher.IsKeyId(keyId))
        {
            return null;
        }

        if (!_privateKeys.TryGetValue(keyId, out var pkcs8))
        {
            var key = await _context.CameraKeys.Find(k => k.KeyId == keyId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                return null;
            }

            using var sealedKey = new MemoryStream(key.EncryptedPrivateKey, writable: false);
            using var plain = new MemoryStream();
            await _encryptor.DecryptAsync(sealedKey, plain, cancellationToken).ConfigureAwait(false);
            pkcs8 = plain.ToArray();
            _privateKeys[keyId] = pkcs8;
        }

        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        return rsa;
    }

    private Task<CameraKey?> OldestAsync(CancellationToken cancellationToken)
        => _context.CameraKeys.Find(_ => true).SortBy(k => k.CreatedAt).FirstOrDefaultAsync(cancellationToken)!;

    private async Task<CameraKey> CreateAsync(CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create(KeySizeBits);
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        try
        {
            using var plain = new MemoryStream(pkcs8, writable: false);
            using var sealedKey = new MemoryStream();
            var header = await _encryptor.EncryptAsync(plain, sealedKey, cancellationToken).ConfigureAwait(false);

            var now = _clock.UtcNow;
            var key = new CameraKey
            {
                KeyId = $"cam-{now:yyyyMMdd}-{Guid.NewGuid():N}"[..29],
                PublicKey = rsa.ExportSubjectPublicKeyInfo(),
                EncryptedPrivateKey = sealedKey.ToArray(),
                KekId = header.KekId,
                CreatedAt = now,
            };

            await _context.CameraKeys.InsertOneAsync(key, cancellationToken).ConfigureAwait(false);

            // Another instance may have made one at the same moment: the
            // oldest is the one everybody hands out.
            return await OldestAsync(cancellationToken).ConfigureAwait(false) ?? key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
