using NeoPrint.Crypto.Releases;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The public release keys every kiosk build trusts (docs/RELEASE_SIGNING.md).
/// </summary>
/// <remarks>
/// <para>
/// Two keys: the one releases are signed with today and the next one, kept
/// offline and unused. When the current key is lost or leaks, releases move to
/// the next key without rebuilding a single kiosk, and the following agent
/// release replaces the old key with a newly generated next one.
/// </para>
/// <para>
/// Compiled in, never read from configuration: a kiosk is an untrusted device,
/// and a key that a settings file could replace would let anyone who reaches
/// the disk install their own build. Only public keys belong here; the private
/// keys never leave the owner's offline machine.
/// </para>
/// <para>
/// Empty until the owner generates the key pairs: with no trusted key every
/// package is refused.
/// </para>
/// </remarks>
public static class ReleaseKeys
{
    /// <summary>The key releases are signed with today.</summary>
    public const string Current = "";

    /// <summary>The next key: generated with the current one, kept offline, used only after a rotation.</summary>
    public const string Next = "";

    /// <summary>The keys above that are set.</summary>
    public static IReadOnlyList<ReleasePublicKey> Trusted { get; } =
        new[] { Current, Next }.Where(pem => pem.Length > 0).Select(ReleasePublicKey.FromPem).ToList();
}
