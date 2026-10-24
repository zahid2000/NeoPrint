namespace NeoPrint.Kiosk.Core.Updates;

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
