using MediatR;

using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Releases;

using ReleaseComponent = NeoPrint.Domain.Releases.ReleaseComponent;

namespace NeoPrint.Application.Releases;

// ---------------------------------------------------------------------------
// Persistence and settings
// ---------------------------------------------------------------------------

/// <summary>The <c>releases</c> collection (plan section 5).</summary>
public interface IReleaseRepository
{
    Task<Release?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The release of this component, platform, key set and version, if there is one.</summary>
    Task<Release?> FindAsync(
        ReleaseComponent component, string platform, string keySet, string version, CancellationToken cancellationToken = default);

    /// <summary>Every release, newest upload first.</summary>
    Task<IReadOnlyList<Release>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Release>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    /// <summary>The SHA-256 of every withdrawn package of a platform.</summary>
    Task<IReadOnlyList<string>> ListWithdrawnShaAsync(string platform, CancellationToken cancellationToken = default);

    Task AddAsync(Release release, CancellationToken cancellationToken = default);

    Task UpdateAsync(Release release, CancellationToken cancellationToken = default);
}

/// <summary>The <c>release_rollouts</c> collection (plan section 5).</summary>
public interface IReleaseRolloutRepository
{
    Task<ReleaseRollout?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Every rollout, newest first.</summary>
    Task<IReadOnlyList<ReleaseRollout>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReleaseRollout>> ListForReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default);

    /// <summary>The active rollouts of a platform: what a kiosk can be offered.</summary>
    Task<IReadOnlyList<ReleaseRollout>> ListOfferingAsync(string platform, CancellationToken cancellationToken = default);

    Task AddAsync(ReleaseRollout rollout, CancellationToken cancellationToken = default);

    Task UpdateAsync(ReleaseRollout rollout, CancellationToken cancellationToken = default);
}

/// <summary>
/// The release key set this backend accepts (T-316): the test server and the
/// development stack take staging packages, production takes production ones.
/// A package signed for the other set is refused at upload.
/// </summary>
public interface IReleaseKeys
{
    ReleaseKeySet Accepted { get; }
}

/// <summary><c>Releases:*</c> settings.</summary>
public sealed class ReleaseSettings
{
    public const string SectionName = "Releases";

    /// <summary><c>production</c> (the default) or <c>staging</c>.</summary>
    public string KeySet { get; set; } = "production";

    /// <summary>The largest package accepted, in megabytes.</summary>
    public int MaxPackageMb { get; set; } = 250;
}

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>
/// Uploads a signed package: the package, its manifest and the manifest's
/// signature, checked here exactly as a kiosk will check them.
/// </summary>
public sealed record UploadReleaseCommand(
    Guid AdminUserId,
    Stream Package,
    string PackageFileName,
    byte[] Manifest,
    byte[] Signature,
    ReleaseChannel Channel,
    string? Ip = null) : IRequest<ReleaseView>;

public sealed record ListReleasesQuery : IRequest<ReleasesPageView>;

/// <summary>From the pilot kiosks to everyone.</summary>
public sealed record PromoteReleaseCommand(Guid AdminUserId, Guid ReleaseId, string? Ip = null)
    : IRequest<ReleaseView>, Behaviors.ITransactionalRequest;

/// <summary>No kiosk installs the package any more; its rollouts are taken back.</summary>
public sealed record WithdrawReleaseCommand(Guid AdminUserId, Guid ReleaseId, string Reason, string? Ip = null)
    : IRequest<ReleaseView>, Behaviors.ITransactionalRequest;

/// <summary>Offers a release to some kiosks.</summary>
public sealed record CreateRolloutCommand(
    Guid AdminUserId,
    Guid ReleaseId,
    RolloutTarget Target,
    IReadOnlyCollection<Guid>? KioskIds,
    int? Percent,
    ReleaseChannel? Channel,
    string? Ip = null,
    bool Urgent = false) : IRequest<RolloutView>, Behaviors.ITransactionalRequest;

public enum RolloutAction
{
    Pause,
    Resume,
    Complete,
    RollBack,
}

public sealed record ChangeRolloutCommand(Guid AdminUserId, Guid RolloutId, RolloutAction Action, string? Ip = null)
    : IRequest<RolloutView>, Behaviors.ITransactionalRequest;

/// <summary>Which releases reach a kiosk first.</summary>
public sealed record SetKioskUpdateChannelCommand(Guid AdminUserId, Guid KioskId, ReleaseChannel Channel, string? Ip = null)
    : IRequest<KioskVersionView>, Behaviors.ITransactionalRequest;

/// <summary>Every kiosk the account can see, with its versions and update state.</summary>
public sealed record ListKioskVersionsQuery(Guid AdminUserId) : IRequest<IReadOnlyList<KioskVersionView>>;

/// <summary>What the kiosk asking should run (<c>GET kiosks/me/update</c>).</summary>
public sealed record GetKioskUpdateQuery(Guid DeviceId) : IRequest<KioskUpdateView>;

/// <summary>A package the kiosk asking is offered; anything else is "not found".</summary>
public sealed record OpenReleasePackageQuery(Guid DeviceId, Guid ReleaseId) : IRequest<ReleaseDownload>;

/// <summary>Where an update of one component stands on the kiosk (<c>POST kiosks/me/update-status</c>).</summary>
public sealed record ReportKioskUpdateCommand(
    Guid DeviceId,
    ReleaseComponent Component,
    string Version,
    KioskUpdateStatus Status,
    string? MessageKey,
    string? Ip = null) : IRequest<Unit>, Behaviors.ITransactionalRequest;

// ---------------------------------------------------------------------------
// Views
// ---------------------------------------------------------------------------

public sealed record ReleaseView(
    Guid Id,
    string Component,
    string Platform,
    string KeySet,
    string Version,
    string MinAgentVersion,
    string Channel,
    string FileName,
    long Size,
    string Sha256,
    string KeyId,
    string? Commit,
    DateTime BuiltAt,
    DateTime UploadedAt,
    bool IsWithdrawn,
    DateTime? WithdrawnAt,
    string? WithdrawReason,
    int InstalledOn);

public sealed record RolloutView(
    Guid Id,
    Guid ReleaseId,
    string Component,
    string Platform,
    string Version,
    string Target,
    IReadOnlyList<Guid> KioskIds,
    int? Percent,
    string? Channel,
    string Status,
    DateTime CreatedAt,
    DateTime? EndedAt,
    int Reaches,
    bool Urgent = false);

/// <summary>The releases page: the key set this server accepts, the releases and the rollouts.</summary>
public sealed record ReleasesPageView(
    string KeySet,
    int TrustedKeys,
    IReadOnlyList<ReleaseView> Releases,
    IReadOnlyList<RolloutView> Rollouts);

public sealed record KioskUpdateStateView(string Component, string Version, string Status, string? Message, DateTime ReportedAt);

public sealed record KioskVersionView(
    Guid KioskId,
    string Code,
    string Name,
    string? Platform,
    string Channel,
    string? AppVersion,
    string? AgentVersion,
    string? ProvisioningVersion,
    IReadOnlyList<KioskUpdateStateView> Updates,
    IReadOnlyList<KioskOfferView> Offered,
    DateTime? LastHeartbeatAt);

public sealed record KioskOfferView(string Component, string Version);

/// <summary>One package a kiosk should run, with what it needs to check it.</summary>
/// <param name="Manifest">The signed manifest, exactly as signed.</param>
/// <param name="Signature">The detached signature file.</param>
/// <param name="Download">Where to fetch the package, relative to the API.</param>
/// <param name="Urgent">A security fix: applied at the first idle moment, not only in the update window (T-321).</param>
public sealed record KioskReleaseOffer(
    string Component,
    Guid ReleaseId,
    string Version,
    string Manifest,
    string Signature,
    long Size,
    string Download,
    bool Urgent = false);

/// <param name="Withdrawn">SHA-256 of every withdrawn package of the kiosk's platform: never install them.</param>
public sealed record KioskUpdateView(
    string? Platform,
    string KeySet,
    IReadOnlyList<KioskReleaseOffer> Offers,
    IReadOnlyList<string> Withdrawn);

public sealed record ReleaseDownload(string FileName, long Size, Stream Content);
