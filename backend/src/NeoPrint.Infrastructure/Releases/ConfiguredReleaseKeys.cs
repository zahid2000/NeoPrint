using Microsoft.Extensions.Options;

using NeoPrint.Application.Releases;
using NeoPrint.Crypto.Releases;
using NeoPrint.ReleaseKeySets;

namespace NeoPrint.Infrastructure.Releases;

/// <summary>
/// The release key set this backend accepts, chosen by <c>Releases:KeySet</c>
/// (T-316). The public keys are the kiosk's own key set files, compiled in
/// here as well, so the server checks an upload with exactly the keys the
/// kiosks of that set carry.
/// </summary>
/// <remarks>
/// Unlike the kiosk, the server may know both sets: it is not an untrusted
/// device, and which set it accepts is a deployment decision - the test server
/// takes staging packages, production takes production ones. A value that is
/// neither stops the start.
/// </remarks>
public sealed class ConfiguredReleaseKeys : IReleaseKeys
{
    public ConfiguredReleaseKeys(IOptions<ReleaseSettings> settings)
    {
        Accepted = settings.Value.KeySet switch
        {
            ProductionReleaseKeys.Name =>
                ReleaseKeySet.FromPems(ProductionReleaseKeys.Name, ProductionReleaseKeys.Current, ProductionReleaseKeys.Next),
            StagingReleaseKeys.Name =>
                ReleaseKeySet.FromPems(StagingReleaseKeys.Name, StagingReleaseKeys.Current, StagingReleaseKeys.Next),
            var other => throw new InvalidOperationException(
                $"Releases:KeySet is '{other}'; it is {ProductionReleaseKeys.Name} or {StagingReleaseKeys.Name} (docs/RELEASE_SIGNING.md)."),
        };
    }

    public ReleaseKeySet Accepted { get; }
}
