using System.Security.Cryptography;

namespace NeoPrint.Application.Kiosks;

/// <summary>
/// The kiosk camera's key pair (T-482): kiosks seal their recordings for it,
/// only the backend opens them.
/// </summary>
public interface ICameraKeys
{
    /// <summary>The public key kiosks seal with; made the first time it is asked for.</summary>
    Task<CameraPublicKey> CurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>The private key a segment names, or null when there is none by that id. The caller disposes it.</summary>
    Task<RSA?> OpenPrivateKeyAsync(string keyId, CancellationToken cancellationToken = default);
}

/// <param name="KeyId">The name segments sealed for it carry.</param>
/// <param name="SubjectPublicKeyInfo">The public key, X.509 SubjectPublicKeyInfo (DER), base64.</param>
public sealed record CameraPublicKey(string KeyId, string SubjectPublicKeyInfo);
