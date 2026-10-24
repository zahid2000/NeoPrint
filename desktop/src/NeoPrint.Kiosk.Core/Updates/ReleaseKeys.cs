using NeoPrint.Crypto.Releases;
using NeoPrint.ReleaseKeySets;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The public release keys this kiosk build trusts (docs/RELEASE_SIGNING.md).
/// </summary>
/// <remarks>
/// <para>
/// Two keys: the one releases are signed with today and the next one, kept
/// offline and unused. When the current key is lost or leaks, releases move to
/// the next key without rebuilding a single kiosk, and the following agent
/// release replaces the old key with a newly generated next one.
/// </para>
/// <para>
/// Which set - production or staging - is decided when the kiosk is built
/// (<c>-p:ReleaseKeySet=staging|production</c>, production by default; T-316):
/// the project compiles only that set's file, so a production build holds no
/// staging key, not even its name. Never read from configuration: a kiosk is
/// an untrusted device, and a key that a settings file could replace would let
/// anyone who reaches the disk install their own build.
/// </para>
/// </remarks>
public static class ReleaseKeys
{
    /// <summary>The set compiled into this build.</summary>
    public static ReleaseKeySet BuiltIn { get; } =
#if NEOPRINT_RELEASE_KEYS_STAGING
        ReleaseKeySet.FromPems(StagingReleaseKeys.Name, StagingReleaseKeys.Current, StagingReleaseKeys.Next);
#else
        ReleaseKeySet.FromPems(ProductionReleaseKeys.Name, ProductionReleaseKeys.Current, ProductionReleaseKeys.Next);
#endif
}
