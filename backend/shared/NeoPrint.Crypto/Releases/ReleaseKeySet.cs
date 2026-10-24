namespace NeoPrint.Crypto.Releases;

/// <summary>
/// The release keys one kind of kiosk build trusts (docs/RELEASE_SIGNING.md,
/// T-316): the pilot kiosks' production set or the test server's and the
/// development stack's set. A build carries exactly one set, chosen when it is
/// built (<c>-p:ReleaseKeySet=</c>), never by configuration.
/// </summary>
/// <remarks>
/// This library goes into every kiosk build, so it names no set: a production
/// build must not hold even the other set's name. The names live in the kiosk
/// project's key set files and in tools/release-sign.
/// </remarks>
public sealed record ReleaseKeySet(string Name, IReadOnlyList<ReleasePublicKey> Keys)
{
    /// <summary>The set from its PEM texts; empty texts are keys not yet made.</summary>
    public static ReleaseKeySet FromPems(string name, params string[] pems)
    {
        if (!IsName(name))
        {
            throw new ArgumentException($"'{name}' is not a key set name (3-20 lower-case letters).", nameof(name));
        }

        return new ReleaseKeySet(name, pems.Where(pem => pem.Length > 0).Select(ReleasePublicKey.FromPem).ToList());
    }

    public static bool IsName(string name) => name.Length is >= 3 and <= 20 && name.All(char.IsAsciiLetterLower);

    public bool Holds(string keyId) => Keys.Any(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal));
}
