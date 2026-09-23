using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Abstractions;

/// <summary>Persistence for kiosk devices.</summary>
public interface IKioskDeviceRepository
{
    Task<KioskDevice?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<KioskDevice?> FindByPublicKeyAsync(string publicKey, CancellationToken cancellationToken = default);

    /// <summary>Finds a pending device by the hash of the code an admin typed in.</summary>
    Task<KioskDevice?> FindByPairingCodeHashAsync(string pairingCodeHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KioskDevice>> ListAsync(
        DeviceStatus? status, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many devices are waiting to be activated.
    /// </summary>
    /// <remarks>
    /// Registration is anonymous, so the total is capped to stop the collection
    /// being filled up (plan section 7.11).
    /// </remarks>
    Task<long> CountPendingAsync(CancellationToken cancellationToken = default);

    Task AddAsync(KioskDevice device, CancellationToken cancellationToken = default);

    Task UpdateAsync(KioskDevice device, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for enrollment tokens.</summary>
public interface IEnrollmentTokenRepository
{
    Task<EnrollmentToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnrollmentToken>> ListAsync(CancellationToken cancellationToken = default);

    Task AddAsync(EnrollmentToken token, CancellationToken cancellationToken = default);

    Task UpdateAsync(EnrollmentToken token, CancellationToken cancellationToken = default);
}

/// <summary>Persistence for kiosks.</summary>
public interface IKioskRepository
{
    Task<Kiosk?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Kiosk?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Kiosk>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The highest numeric suffix in use, for generating the next kiosk code.</summary>
    Task<int> GetHighestCodeSequenceAsync(CancellationToken cancellationToken = default);

    Task AddAsync(Kiosk kiosk, CancellationToken cancellationToken = default);

    Task UpdateAsync(Kiosk kiosk, CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies the signature a device produces over a challenge.
/// </summary>
/// <remarks>
/// The device holds an ECDSA P-256 private key it generated itself and that
/// never leaves the machine - it lives in the TPM where there is one. The
/// backend only ever holds the public key (plan section 7.11).
/// </remarks>
public interface IDeviceSignatureVerifier
{
    /// <summary>
    /// Checks a signature over a nonce against a public key in SPKI form.
    /// </summary>
    /// <param name="publicKeySpkiBase64">The device's public key.</param>
    /// <param name="nonce">The challenge the device was given.</param>
    /// <param name="signatureBase64">The device's signature over it.</param>
    bool Verify(string publicKeySpkiBase64, string nonce, string signatureBase64);

    /// <summary>Whether a string is a usable ECDSA P-256 public key.</summary>
    bool IsValidPublicKey(string publicKeySpkiBase64);
}

/// <summary>
/// Issues and redeems the one-time challenges a device signs.
/// </summary>
/// <remarks>
/// Held in Redis with a short lifetime: a challenge is valid once, so a
/// captured signature cannot be replayed.
/// </remarks>
public interface IDeviceChallengeStore
{
    Task<string> IssueAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Consumes a challenge, returning false if it is unknown or already used.</summary>
    Task<bool> RedeemAsync(Guid deviceId, string nonce, CancellationToken cancellationToken = default);
}
