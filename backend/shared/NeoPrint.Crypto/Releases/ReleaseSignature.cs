using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NeoPrint.Crypto.Releases;

/// <summary>
/// A public release key a kiosk trusts (docs/RELEASE_SIGNING.md): ECDSA on
/// P-256, identified by the first 16 hex digits of the SHA-256 of its
/// SubjectPublicKeyInfo.
/// </summary>
public sealed class ReleasePublicKey
{
    private const string P256 = "1.2.840.10045.3.1.7";

    private readonly byte[] _subjectPublicKeyInfo;

    private ReleasePublicKey(byte[] subjectPublicKeyInfo)
    {
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        KeyId = IdOf(subjectPublicKeyInfo);
    }

    public string KeyId { get; }

    /// <summary>Reads a <c>-----BEGIN PUBLIC KEY-----</c> PEM; anything but a P-256 public key is refused.</summary>
    public static ReleasePublicKey FromPem(string pem)
    {
        if (pem.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("This is a private key. Only the public key is given out.", nameof(pem));
        }

        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException)
        {
            throw new ArgumentException("The text is not an ECDSA public key in PEM.", nameof(pem), e);
        }

        return FromKey(key);
    }

    /// <summary>The public half of <paramref name="key"/>, which may hold the private half too.</summary>
    public static ReleasePublicKey FromKey(ECDsa key)
    {
        if (key.ExportParameters(includePrivateParameters: false).Curve.Oid?.Value != P256)
        {
            throw new ArgumentException("A release key is ECDSA on the P-256 curve.", nameof(key));
        }

        return new ReleasePublicKey(key.ExportSubjectPublicKeyInfo());
    }

    public string ToPem() => PemEncoding.WriteString("PUBLIC KEY", _subjectPublicKeyInfo);

    internal bool Verifies(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    internal static string IdOf(byte[] subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo), 0, 8).ToLowerInvariant();
}

/// <summary>
/// The detached signature of a manifest (<c>manifest.json.sig</c>):
/// <c>{ "keyId": ..., "algorithm": "ES256", "signature": base64 }</c>.
/// </summary>
/// <remarks>
/// What is signed is a fixed context line followed by the manifest's exact
/// bytes, so a release key's signature means "this is a NeoPrint release
/// manifest" and nothing else - it cannot be lifted onto another kind of
/// document signed with the same key.
/// </remarks>
public sealed record ReleaseSignature(string KeyId, byte[] Signature)
{
    public const string Algorithm = "ES256";

    private static readonly byte[] Context = "neoprint-release-manifest-v1\n"u8.ToArray();

    /// <summary>Signs a manifest. Used only by the offline signing tool (tools/release-sign).</summary>
    public static ReleaseSignature Sign(ECDsa privateKey, ReadOnlySpan<byte> manifest)
    {
        var publicKey = ReleasePublicKey.FromKey(privateKey);
        var signature = privateKey.SignData(Signed(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new ReleaseSignature(publicKey.KeyId, signature);
    }

    public bool IsValidFor(ReadOnlySpan<byte> manifest, ReleasePublicKey key) =>
        string.Equals(key.KeyId, KeyId, StringComparison.Ordinal) && key.Verifies(Signed(manifest), Signature);

    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        // Base64 is plain ASCII; the default encoder would write '+' as +.
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("keyId", KeyId);
            json.WriteString("algorithm", Algorithm);
            json.WriteString("signature", Convert.ToBase64String(Signature));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    public static ReleaseSignature Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 3
                || root.GetProperty("algorithm").GetString() != Algorithm
                || root.GetProperty("keyId").GetString() is not { Length: 16 } keyId
                || keyId.Any(c => !char.IsAsciiHexDigitLower(c))
                || root.GetProperty("signature").GetString() is not { } text)
            {
                throw new FormatException("The signature file is not an ES256 release signature.");
            }

            var signature = Convert.FromBase64String(text);
            return signature.Length == 64
                ? new ReleaseSignature(keyId, signature)
                : throw new FormatException("An ES256 signature is 64 bytes.");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("The signature file is not an ES256 release signature.", e);
        }
    }

    private static byte[] Signed(ReadOnlySpan<byte> manifest) => [.. Context, .. manifest];
}
