// Its own namespace: these files are compiled into the kiosk, tools/release-sign and the API,
// and a NeoPrint.Kiosk namespace in the API would shadow its Kiosk type.
namespace NeoPrint.ReleaseKeySets;

/// <summary>
/// The production release keys: the only ones a pilot kiosk build carries
/// (<c>-p:ReleaseKeySet=production</c>, the default; docs/RELEASE_SIGNING.md).
/// </summary>
/// <remarks>
/// Made by the owner on an offline machine (T-315). Empty until then: a
/// production build accepts no package at all. Only public keys belong here.
/// </remarks>
internal static class ProductionReleaseKeys
{
    public const string Name = "production";

    /// <summary>The key releases are signed with today.</summary>
    public const string Current = "";

    /// <summary>The next key: made with the current one, kept offline, used only after a rotation.</summary>
    public const string Next = "";
}
