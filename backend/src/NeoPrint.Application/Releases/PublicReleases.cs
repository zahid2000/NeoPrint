using System.Text;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Releases;

using ReleaseComponent = NeoPrint.Domain.Releases.ReleaseComponent;

namespace NeoPrint.Application.Releases;

// ---------------------------------------------------------------------------
// The newest release, for anybody (T-327)
//
// A provisioning script on a new kiosk has no identity yet, so it asks
// anonymously: what is the newest release of a component for a platform, and
// the release's three files. It checks the signature itself, with the keys
// the setup stick carries - the server is not trusted for that. What it is
// shown is narrow on purpose: a production-signed release that is published
// (on the stable channel, or the pilot one when asked for), not withdrawn and
// never rolled back, and nothing about who uploaded it or where it went. A
// server that takes staging packages - the test server, development - shows
// none at all.
// ---------------------------------------------------------------------------

/// <param name="Channel"><c>stable</c> (the default) or <c>pilot</c>, which also takes what is stable.</param>
public sealed record GetPublicReleaseQuery(string Component, string Platform, string? Channel) : IRequest<PublicReleaseView>;

/// <summary>One of a public release's files: <c>manifest</c>, <c>signature</c> or <c>package</c>.</summary>
public sealed record OpenPublicReleaseFileQuery(string Component, string Platform, string Version, string File) : IRequest<ReleaseDownload>;

/// <summary>What the script needs to fetch and check a release; nothing of who uploaded it or where it went.</summary>
public sealed record PublicReleaseView(
    string Component,
    string Platform,
    string Version,
    string Channel,
    string KeySet,
    string KeyId,
    string MinAgentVersion,
    string FileName,
    long Size,
    string Sha256,
    DateTime BuiltAt,
    string ManifestPath,
    string SignaturePath,
    string PackagePath);

/// <inheritdoc cref="GetPublicReleaseQuery" />
public sealed class PublicReleaseHandlers :
    IRequestHandler<GetPublicReleaseQuery, PublicReleaseView>,
    IRequestHandler<OpenPublicReleaseFileQuery, ReleaseDownload>
{
    /// <summary>The only key set whose releases are shown to anybody.</summary>
    public const string PublicKeySet = "production";

    private readonly IReleaseRepository _releases;
    private readonly IReleaseRolloutRepository _rollouts;
    private readonly IReleaseKeys _keys;
    private readonly IFileStorage _storage;

    public PublicReleaseHandlers(IReleaseRepository releases, IReleaseRolloutRepository rollouts, IReleaseKeys keys, IFileStorage storage)
    {
        _releases = releases;
        _rollouts = rollouts;
        _keys = keys;
        _storage = storage;
    }

    public async Task<PublicReleaseView> Handle(GetPublicReleaseQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (component, pilot) = Parse(request.Component, request.Channel);
        var release = (await PublicAsync(component, request.Platform, pilot, cancellationToken).ConfigureAwait(false))
                      .MaxBy(candidate => ReleaseVersion.Parse(candidate.Version))
                      ?? throw new NotFoundException("release");

        var path = $"api/releases/{Name(release.Component)}/{release.Platform}/{release.Version}";
        return new PublicReleaseView(
            Name(release.Component),
            release.Platform,
            release.Version,
            release.Channel == ReleaseChannel.Stable ? "stable" : "pilot",
            release.KeySet,
            release.KeyId,
            release.MinAgentVersion,
            release.FileName,
            release.Size,
            release.Sha256,
            release.BuiltAt,
            $"{path}/manifest",
            $"{path}/signature",
            $"{path}/package");
    }

    public async Task<ReleaseDownload> Handle(OpenPublicReleaseFileQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The same rule as the newest: a file of a release nobody is shown is not found.
        var (component, _) = Parse(request.Component, "pilot");
        var release = (await PublicAsync(component, request.Platform, pilot: true, cancellationToken).ConfigureAwait(false))
                      .FirstOrDefault(candidate => candidate.Version == request.Version)
                      ?? throw new NotFoundException("release");

        switch (request.File)
        {
            case "manifest":
                return Text($"{release.FileName}.manifest.json", release.Manifest);
            case "signature":
                return Text($"{release.FileName}.manifest.json.sig", release.Signature);
            case "package":
                var stream = await _storage
                    .OpenReadAsync(
                        new StoredObject(release.Package.ProviderId, release.Package.Key, release.Package.StoredSha256,
                            release.Package.EncryptedLength, release.Size, release.Package.KekId),
                        StorageBucket.Files,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ReleaseDownload(release.FileName, release.Size, stream);
            default:
                throw new NotFoundException("release");
        }
    }

    /// <summary>The releases anybody may see: production, published, still out, never taken back, and signed by this server's keys.</summary>
    private async Task<List<Release>> PublicAsync(ReleaseComponent component, string platform, bool pilot, CancellationToken cancellationToken)
    {
        var accepted = _keys.Accepted;
        if (accepted.Name != PublicKeySet)
        {
            return [];
        }

        var candidates = (await _releases.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(release => release.Component == component
                              && string.Equals(release.Platform, platform, StringComparison.Ordinal)
                              && release.KeySet == PublicKeySet
                              && !release.IsWithdrawn
                              && (pilot || release.Channel == ReleaseChannel.Stable))
            .ToList();

        var shown = new List<Release>();
        foreach (var release in candidates)
        {
            // A rollout of it that was taken back says something went wrong on a kiosk.
            if ((await _rollouts.ListForReleaseAsync(release.Id, cancellationToken).ConfigureAwait(false))
                .Any(rollout => rollout.Status == RolloutStatus.RolledBack))
            {
                continue;
            }

            // Checked again as it is handed out: what was stored is what was signed.
            var check = new ReleaseVerifier(accepted.Keys).CheckManifest(
                Encoding.UTF8.GetBytes(release.Manifest),
                Encoding.UTF8.GetBytes(release.Signature),
                new ReleaseExpectation(
                    Enum.Parse<NeoPrint.Crypto.Releases.ReleaseComponent>(release.Component.ToString()),
                    release.Platform, accepted.Name, null, ReleaseVersion.Parse(release.MinAgentVersion), []));
            if (check.Accepted && check.Manifest!.Sha256 == release.Sha256)
            {
                shown.Add(release);
            }
        }

        return shown;
    }

    private static (ReleaseComponent Component, bool Pilot) Parse(string component, string? channel)
    {
        var parsed = component?.ToLowerInvariant() switch
        {
            "app" => ReleaseComponent.App,
            "agent" => ReleaseComponent.Agent,
            "provisioning" => ReleaseComponent.Provisioning,
            _ => throw new NotFoundException("release"),
        };

        return channel?.ToLowerInvariant() switch
        {
            null or "" or "stable" => (parsed, false),
            "pilot" => (parsed, true),
            _ => throw new ValidationException([new ValidationError("channel", "releases.channelInvalid")]),
        };
    }

    private static string Name(ReleaseComponent component) => component.ToString().ToLowerInvariant();

    private static ReleaseDownload Text(string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ReleaseDownload(fileName, bytes.Length, new MemoryStream(bytes, writable: false));
    }
}
