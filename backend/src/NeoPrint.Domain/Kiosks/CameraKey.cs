using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// The key pair kiosk camera recordings are sealed with (T-482): kiosks get
/// the public half in their configuration; the private half stays here,
/// itself encrypted with the backend's key encryption key.
/// </summary>
/// <remarks>
/// Created the first time a kiosk needs it. Should two API instances create
/// one at the same moment, both are kept - a segment names the key it was
/// sealed for - and the oldest is the one handed out.
/// </remarks>
public sealed class CameraKey : Entity
{
    /// <summary>The name written in the clear in every segment sealed for it.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The public key, X.509 SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>The private key, PKCS#8 (DER), as an envelope object under the backend's key encryption key.</summary>
    public byte[] EncryptedPrivateKey { get; set; } = [];

    /// <summary>The key encryption key the private key is wrapped with.</summary>
    public string KekId { get; set; } = string.Empty;
}
