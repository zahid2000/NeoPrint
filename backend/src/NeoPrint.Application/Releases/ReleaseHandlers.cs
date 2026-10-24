using System.Security.Cryptography;
using System.Text;

using MediatR;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Releases;

using ReleaseComponent = NeoPrint.Domain.Releases.ReleaseComponent;

namespace NeoPrint.Application.Releases;

/// <summary>
/// Releases and rollouts, from the panel's side and the kiosk's (plan section
/// 7.12; docs/RELEASE_SIGNING.md; T-317).
/// </summary>
public sealed class ReleaseHandlers :
    IRequestHandler<UploadReleaseCommand, ReleaseView>,
    IRequestHandler<ListReleasesQuery, ReleasesPageView>,
    IRequestHandler<PromoteReleaseCommand, ReleaseView>,
    IRequestHandler<WithdrawReleaseCommand, ReleaseView>,
    IRequestHandler<CreateRolloutCommand, RolloutView>,
    IRequestHandler<ChangeRolloutCommand, RolloutView>,
    IRequestHandler<SetKioskUpdateChannelCommand, KioskVersionView>,
    IRequestHandler<ListKioskVersionsQuery, IReadOnlyList<KioskVersionView>>,
    IRequestHandler<GetKioskUpdateQuery, KioskUpdateView>,
    IRequestHandler<OpenReleasePackageQuery, ReleaseDownload>,
    IRequestHandler<ReportKioskUpdateCommand, Unit>
{
    /// <summary>A manifest or a signature is a few hundred bytes.</summary>
    public const int MaxSmallFile = 16 * 1024;

    private const int MaxRolloutKiosks = 500;

    private readonly IReleaseRepository _releases;
    private readonly IReleaseRolloutRepository _rollouts;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IFileStorage _storage;
    private readonly IReleaseKeys _keys;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly IAdminNotifier _admins;
    private readonly IKioskNotifier _kioskHub;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ReleaseSettings _settings;
    private readonly ILogger<ReleaseHandlers> _logger;

    public ReleaseHandlers(
        IReleaseRepository releases,
        IReleaseRolloutRepository rollouts,
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IFileStorage storage,
        IReleaseKeys keys,
        IPermissionResolver permissions,
        IAuditWriter audit,
        IAdminNotifier admins,
        IKioskNotifier kioskHub,
        ICorrelationContext correlation,
        IClock clock,
        IOptions<ReleaseSettings> settings,
        ILogger<ReleaseHandlers> logger)
    {
        _releases = releases;
        _rollouts = rollouts;
        _kiosks = kiosks;
        _devices = devices;
        _storage = storage;
        _keys = keys;
        _permissions = permissions;
        _audit = audit;
        _admins = admins;
        _kioskHub = kioskHub;
        _correlation = correlation;
        _clock = clock;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc cref="UploadReleaseCommand" />
    public async Task<ReleaseView> Handle(UploadReleaseCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Manifest.Length is 0 or > MaxSmallFile)
        {
            throw new ValidationException([new ValidationError("manifest", "releases.manifestMissing")]);
        }

        if (request.Signature.Length is 0 or > MaxSmallFile)
        {
            throw new ValidationException([new ValidationError("signature", "releases.signatureMissing")]);
        }

        // Checked as a kiosk of this server's key set will check it: the
        // signature first, nothing of the manifest believed before it.
        var accepted = _keys.Accepted;
        ReleaseManifest claimed;
        try
        {
            claimed = ReleaseManifest.Parse(request.Manifest);
        }
        catch (FormatException)
        {
            throw new ValidationException([new ValidationError("manifest", "releases.refused.malformedManifest")]);
        }

        var check = new ReleaseVerifier(accepted.Keys).CheckManifest(
            request.Manifest,
            request.Signature,
            new ReleaseExpectation(claimed.Component, claimed.Platform, accepted.Name, null, claimed.MinAgentVersion, []));

        if (!check.Accepted)
        {
            _logger.LogWarning("A release upload was refused: {Rejection}.", check.Rejection);
            throw new ValidationException([new ValidationError(
                check.Rejection is ReleaseRejection.MalformedManifest ? "manifest" : "signature",
                "releases.refused." + char.ToLowerInvariant(check.Rejection.ToString()[0]) + check.Rejection.ToString()[1..],
                ("keySet", accepted.Name))]);
        }

        var manifest = check.Manifest!;
        if (!string.Equals(Path.GetFileName(request.PackageFileName), manifest.File, StringComparison.Ordinal))
        {
            throw new ValidationException([new ValidationError("package", "releases.fileNameMismatch", ("file", manifest.File))]);
        }

        var component = Enum.Parse<ReleaseComponent>(manifest.Component.ToString());
        var version = manifest.Version.ToString();
        if (await _releases.FindAsync(component, manifest.Platform, manifest.KeySet, version, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            // The same package again (a retried upload): the release there is. Another one under the same version: no.
            return existing.Sha256 == manifest.Sha256
                ? await ViewAsync(existing, cancellationToken).ConfigureAwait(false)
                : throw new DomainException(UserMessage.Of("releases.versionTaken", ("version", version)));
        }

        var release = new Release
        {
            Component = component,
            Platform = manifest.Platform,
            KeySet = manifest.KeySet,
            Version = version,
            MinAgentVersion = manifest.MinAgentVersion.ToString(),
            FileName = manifest.File,
            Size = manifest.Size,
            Sha256 = manifest.Sha256,
            Commit = manifest.Commit,
            BuiltAt = manifest.CreatedAt.UtcDateTime,
            KeyId = ReleaseSignature.Parse(request.Signature).KeyId,
            Manifest = Encoding.UTF8.GetString(request.Manifest),
            Signature = Encoding.UTF8.GetString(request.Signature),
            UploadedBy = request.AdminUserId,
            CreatedAt = _clock.UtcNow,
        };
        release.PublishTo(request.Channel);

        // Hashed as it streams into the store; a package that is not the one
        // the manifest names does not stay there.
        var limit = (long)_settings.MaxPackageMb * 1024 * 1024;
        var counted = new CountingHashStream(request.Package, Math.Min(limit, manifest.Size));
        StoredObject stored;
        try
        {
            stored = await _storage
                .WriteAsync(StorageBucket.Files, $"releases/{release.Id:N}/{manifest.File}", counted, "application/octet-stream", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageTooLargeException)
        {
            throw new ValidationException([new ValidationError("package", "releases.refused.packageMismatch", ("keySet", accepted.Name))]);
        }

        if (counted.Length != manifest.Size || counted.Sha256 != manifest.Sha256)
        {
            await _storage.DeleteAsync(stored, StorageBucket.Files, cancellationToken).ConfigureAwait(false);
            throw new ValidationException([new ValidationError("package", "releases.refused.packageMismatch", ("keySet", accepted.Name))]);
        }

        release.Package = new ReleasePackage
        {
            ProviderId = stored.ProviderId,
            Key = stored.Key,
            StoredSha256 = stored.Sha256,
            EncryptedLength = stored.EncryptedLength,
            KekId = stored.KekId,
        };

        await _releases.AddAsync(release, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "releases.upload", nameof(Release), release.Id.ToString(), null,
                    $"{release.Component} {release.Version} {release.Platform} {release.KeySet} {release.Channel}", request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);
        await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);

        return await ViewAsync(release, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ListReleasesQuery" />
    public async Task<ReleasesPageView> Handle(ListReleasesQuery request, CancellationToken cancellationToken)
    {
        var releases = await _releases.ListAsync(cancellationToken).ConfigureAwait(false);
        var rollouts = await _rollouts.ListAsync(cancellationToken).ConfigureAwait(false);
        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);

        return new ReleasesPageView(
            _keys.Accepted.Name,
            _keys.Accepted.Keys.Count,
            [.. releases.Select(release => View(release, kiosks))],
            [.. rollouts.Select(rollout => View(rollout, kiosks))]);
    }

    /// <inheritdoc cref="PromoteReleaseCommand" />
    public async Task<ReleaseView> Handle(PromoteReleaseCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var release = await ReleaseAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (release.Promote())
        {
            await _releases.UpdateAsync(release, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                    new AuditEntry(request.AdminUserId, "releases.promote", nameof(Release), release.Id.ToString(),
                        nameof(ReleaseChannel.Pilot), nameof(ReleaseChannel.Stable), request.Ip, null),
                    cancellationToken)
                .ConfigureAwait(false);
            await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ViewAsync(release, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="WithdrawReleaseCommand" />
    public async Task<ReleaseView> Handle(WithdrawReleaseCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 3 or > 500)
        {
            throw new ValidationException([new ValidationError("reason", "releases.reasonRequired")]);
        }

        var release = await ReleaseAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        if (!release.Withdraw(request.AdminUserId, reason, now))
        {
            return await ViewAsync(release, cancellationToken).ConfigureAwait(false);
        }

        await _releases.UpdateAsync(release, cancellationToken).ConfigureAwait(false);

        // Its rollouts end with it: nothing offers a withdrawn package.
        var actor = ActorContext.Admin(request.AdminUserId, "releases.reason.withdrawn", _correlation.CorrelationId);
        foreach (var rollout in await _rollouts.ListForReleaseAsync(release.Id, cancellationToken).ConfigureAwait(false))
        {
            if (rollout.Status is RolloutStatus.Active or RolloutStatus.Paused)
            {
                rollout.RollBack(actor, now);
                await _rollouts.UpdateAsync(rollout, cancellationToken).ConfigureAwait(false);
            }
        }

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "releases.withdraw", nameof(Release), release.Id.ToString(), null,
                    $"{release.Component} {release.Version} {release.Platform}", request.Ip, reason),
                cancellationToken)
            .ConfigureAwait(false);

        // Every kiosk asks again and learns the package is not to be installed.
        await _kioskHub.UpdateAvailableAsync(null, cancellationToken).ConfigureAwait(false);
        await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);

        return await ViewAsync(release, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CreateRolloutCommand" />
    public async Task<RolloutView> Handle(CreateRolloutCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var release = await ReleaseAsync(request.ReleaseId, cancellationToken).ConfigureAwait(false);
        if (release.IsWithdrawn)
        {
            throw new DomainException("releases.withdrawn");
        }

        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);
        var rollout = new ReleaseRollout
        {
            ReleaseId = release.Id,
            Component = release.Component,
            Platform = release.Platform,
            Version = release.Version,
            Target = request.Target,
            CreatedBy = request.AdminUserId,
            CreatedAt = _clock.UtcNow,
        };

        var problems = new List<ValidationError>();
        switch (request.Target)
        {
            case RolloutTarget.Kiosks:
                var ids = (request.KioskIds ?? []).Distinct().ToList();
                if (ids.Count is 0 or > MaxRolloutKiosks)
                {
                    problems.Add(new ValidationError("kioskIds", "rollouts.kiosksRequired", ("max", MaxRolloutKiosks)));
                }
                else if (ids.Any(id => kiosks.All(kiosk => kiosk.Id != id)))
                {
                    problems.Add(new ValidationError("kioskIds", "rollouts.unknownKiosk"));
                }

                rollout.KioskIds = ids;
                break;
            case RolloutTarget.Percent:
                if (request.Percent is not (>= 1 and <= 100))
                {
                    problems.Add(new ValidationError("percent", "rollouts.percentRange"));
                }

                rollout.Percent = request.Percent;
                break;
            case RolloutTarget.Channel:
                if (request.Channel is null)
                {
                    problems.Add(new ValidationError("channel", "rollouts.channelRequired"));
                }

                rollout.Channel = request.Channel;
                break;
            case RolloutTarget.All:
                break;
            default:
                problems.Add(new ValidationError("target", "rollouts.targetUnknown"));
                break;
        }

        if (problems.Count > 0)
        {
            throw new ValidationException(problems);
        }

        // A pilot release goes only to pilot kiosks or to kiosks picked by name
        // (plan section 7.12, step 2): everyone gets it once it is promoted.
        if (release.Channel == ReleaseChannel.Pilot
            && !(request.Target == RolloutTarget.Kiosks || (request.Target == RolloutTarget.Channel && request.Channel == ReleaseChannel.Pilot)))
        {
            throw new DomainException("rollouts.pilotOnly");
        }

        await _rollouts.AddAsync(rollout, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "rollouts.create", nameof(ReleaseRollout), rollout.Id.ToString(), null,
                    $"{release.Component} {release.Version} {release.Platform} → {Describe(rollout)}", request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        await NotifyReachedAsync(rollout, kiosks, cancellationToken).ConfigureAwait(false);
        await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);

        return View(rollout, kiosks);
    }

    /// <inheritdoc cref="ChangeRolloutCommand" />
    public async Task<RolloutView> Handle(ChangeRolloutCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rollout = await _rollouts.FindByIdAsync(request.RolloutId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("rollout");
        var before = rollout.Status;
        var now = _clock.UtcNow;
        var actor = ActorContext.Admin(request.AdminUserId, correlationId: _correlation.CorrelationId);

        switch (request.Action)
        {
            case RolloutAction.Pause:
                rollout.Pause(actor, now);
                break;
            case RolloutAction.Resume:
                if ((await ReleaseAsync(rollout.ReleaseId, cancellationToken).ConfigureAwait(false)).IsWithdrawn)
                {
                    throw new DomainException("releases.withdrawn");
                }

                rollout.Resume(actor, now);
                break;
            case RolloutAction.Complete:
                rollout.Complete(actor, now);
                break;
            case RolloutAction.RollBack:
                rollout.RollBack(actor, now);
                break;
            default:
                throw new ValidationException([new ValidationError("action", "rollouts.actionUnknown")]);
        }

        var kiosks = await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false);
        if (rollout.Status == before)
        {
            // Pressed twice: the state there is.
            return View(rollout, kiosks);
        }

        await _rollouts.UpdateAsync(rollout, cancellationToken).ConfigureAwait(false);
        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, $"rollouts.{request.Action.ToString().ToLowerInvariant()}", nameof(ReleaseRollout),
                    rollout.Id.ToString(), before.ToString(), rollout.Status.ToString(), request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        await NotifyReachedAsync(rollout, kiosks, cancellationToken).ConfigureAwait(false);
        await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);

        return View(rollout, kiosks);
    }

    /// <inheritdoc cref="SetKioskUpdateChannelCommand" />
    public async Task<KioskVersionView> Handle(SetKioskUpdateChannelCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);
        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        var before = kiosk.UpdateChannel;
        if (before != request.Channel)
        {
            kiosk.UpdateChannel = request.Channel;
            await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                    new AuditEntry(request.AdminUserId, "kiosks.updateChannel", nameof(Kiosk), kiosk.Id.ToString(),
                        before.ToString(), request.Channel.ToString(), request.Ip, null),
                    cancellationToken)
                .ConfigureAwait(false);
            await _kioskHub.UpdateAvailableAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
            await _admins.ReleasesChangedAsync(cancellationToken).ConfigureAwait(false);
        }

        return await VersionViewAsync(kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ListKioskVersionsQuery" />
    public async Task<IReadOnlyList<KioskVersionView>> Handle(ListKioskVersionsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(kiosk => effective.CanSee(kiosk.Id))
            .OrderBy(kiosk => kiosk.Code, StringComparer.Ordinal)
            .ToList();

        var offered = await OfferingAsync(cancellationToken).ConfigureAwait(false);
        return [.. kiosks.Select(kiosk => VersionView(kiosk, Offers(kiosk, offered)))];
    }

    /// <inheritdoc cref="GetKioskUpdateQuery" />
    public async Task<KioskUpdateView> Handle(GetKioskUpdateQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var platform = Rid(kiosk.Platform);
        if (platform is null)
        {
            return new KioskUpdateView(null, _keys.Accepted.Name, [], []);
        }

        var offers = Offers(kiosk, await OfferingAsync(cancellationToken).ConfigureAwait(false));
        var withdrawn = await _releases.ListWithdrawnShaAsync(platform, cancellationToken).ConfigureAwait(false);

        return new KioskUpdateView(
            platform,
            _keys.Accepted.Name,
            [
                .. offers.Select(release => new KioskReleaseOffer(
                    release.Component.ToString(),
                    release.Id,
                    release.Version,
                    release.Manifest,
                    release.Signature,
                    release.Size,
                    $"api/releases/{release.Id}/download")),
            ],
            withdrawn);
    }

    /// <inheritdoc cref="OpenReleasePackageQuery" />
    public async Task<ReleaseDownload> Handle(OpenReleasePackageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only what this kiosk is offered right now: a kiosk is an untrusted
        // device and does not browse the release catalogue.
        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var release = Offers(kiosk, await OfferingAsync(cancellationToken).ConfigureAwait(false))
                          .FirstOrDefault(offer => offer.Id == request.ReleaseId)
                      ?? throw new NotFoundException("release");

        var stream = await _storage
            .OpenReadAsync(
                new StoredObject(release.Package.ProviderId, release.Package.Key, release.Package.StoredSha256,
                    release.Package.EncryptedLength, release.Size, release.Package.KekId),
                StorageBucket.Files,
                cancellationToken)
            .ConfigureAwait(false);

        return new ReleaseDownload(release.FileName, release.Size, stream);
    }

    /// <inheritdoc cref="ReportKioskUpdateCommand" />
    public async Task<Unit> Handle(ReportKioskUpdateCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ReleaseVersion.TryParse(request.Version, out _))
        {
            throw new ValidationException([new ValidationError("version", "releases.versionInvalid")]);
        }

        // A message the catalogue does not know is not shown to anybody.
        var messageKey = request.MessageKey is { } key && Messages.Has(key) ? key : null;
        var kiosk = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (!kiosk.ReportUpdate(request.Component, request.Version, request.Status, messageKey, now))
        {
            return Unit.Value;
        }

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        // What the kiosk did to itself is on the record: installed, refused, taken back.
        if (request.Status is KioskUpdateStatus.Succeeded or KioskUpdateStatus.Failed or KioskUpdateStatus.RolledBack)
        {
            await _audit.WriteAsync(
                    new AuditEntry(null, $"kiosks.update.{request.Status}", nameof(Kiosk), kiosk.Id.ToString(),
                        request.Component.ToString(), request.Version, request.Ip, messageKey, ActorType.Kiosk),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _admins.KioskUpdateReportedAsync(await VersionViewAsync(kiosk, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }

    /// <summary><c>win-x64</c> / <c>linux-x64</c>, or null for a kiosk that has not said.</summary>
    public static string? Rid(KioskPlatform platform) => platform switch
    {
        KioskPlatform.Windows => "win-x64",
        KioskPlatform.Linux => "linux-x64",
        _ => null,
    };

    /// <summary>
    /// What a kiosk is offered: of each component, the newest release an
    /// active rollout reaches it with - not withdrawn, of its platform and of
    /// this server's key set.
    /// </summary>
    private IReadOnlyList<Release> Offers(Kiosk kiosk, Offering offering)
    {
        var platform = Rid(kiosk.Platform);
        if (platform is null)
        {
            return [];
        }

        return
        [
            .. offering.Rollouts
                .Where(rollout => rollout.IsOffering && rollout.Platform == platform && rollout.Reaches(kiosk.Id, kiosk.UpdateChannel))
                .Select(rollout => offering.Releases.GetValueOrDefault(rollout.ReleaseId))
                .OfType<Release>()
                .Where(release => !release.IsWithdrawn && release.KeySet == _keys.Accepted.Name && release.Platform == platform)
                .GroupBy(release => release.Component)
                .Select(group => group.MaxBy(release => ReleaseVersion.Parse(release.Version))!)
                .OrderBy(release => release.Component),
        ];
    }

    private sealed record Offering(IReadOnlyList<ReleaseRollout> Rollouts, IReadOnlyDictionary<Guid, Release> Releases);

    private async Task<Offering> OfferingAsync(CancellationToken cancellationToken)
    {
        var rollouts = new List<ReleaseRollout>();
        foreach (var platform in ReleaseManifest.Platforms)
        {
            rollouts.AddRange(await _rollouts.ListOfferingAsync(platform, cancellationToken).ConfigureAwait(false));
        }

        var releases = await _releases
            .ListByIdsAsync([.. rollouts.Select(rollout => rollout.ReleaseId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        return new Offering(rollouts, releases.ToDictionary(release => release.Id));
    }

    private async Task NotifyReachedAsync(ReleaseRollout rollout, IReadOnlyList<Kiosk> kiosks, CancellationToken cancellationToken)
    {
        foreach (var kiosk in kiosks.Where(kiosk => Rid(kiosk.Platform) == rollout.Platform && rollout.Reaches(kiosk.Id, kiosk.UpdateChannel)))
        {
            await _kioskHub.UpdateAvailableAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Release> ReleaseAsync(Guid id, CancellationToken cancellationToken)
        => await _releases.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("release");

    private async Task<Kiosk> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        return await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("kiosk");
    }

    private async Task<ReleaseView> ViewAsync(Release release, CancellationToken cancellationToken)
        => View(release, await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false));

    private async Task<KioskVersionView> VersionViewAsync(Kiosk kiosk, CancellationToken cancellationToken)
        => VersionView(kiosk, Offers(kiosk, await OfferingAsync(cancellationToken).ConfigureAwait(false)));

    private static ReleaseView View(Release release, IReadOnlyList<Kiosk> kiosks) => new(
        release.Id,
        release.Component.ToString(),
        release.Platform,
        release.KeySet,
        release.Version,
        release.MinAgentVersion,
        release.Channel.ToString(),
        release.FileName,
        release.Size,
        release.Sha256,
        release.KeyId,
        release.Commit,
        release.BuiltAt,
        release.CreatedAt,
        release.IsWithdrawn,
        release.WithdrawnAt,
        release.WithdrawReason,
        kiosks.Count(kiosk => Rid(kiosk.Platform) == release.Platform && InstalledVersion(kiosk, release.Component) == release.Version));

    private static RolloutView View(ReleaseRollout rollout, IReadOnlyList<Kiosk> kiosks) => new(
        rollout.Id,
        rollout.ReleaseId,
        rollout.Component.ToString(),
        rollout.Platform,
        rollout.Version,
        rollout.Target.ToString(),
        rollout.KioskIds,
        rollout.Percent,
        rollout.Channel?.ToString(),
        rollout.Status.ToString(),
        rollout.CreatedAt,
        rollout.EndedAt,
        kiosks.Count(kiosk => Rid(kiosk.Platform) == rollout.Platform && rollout.Reaches(kiosk.Id, kiosk.UpdateChannel)));

    private static KioskVersionView VersionView(Kiosk kiosk, IReadOnlyList<Release> offers) => new(
        kiosk.Id,
        kiosk.Code,
        kiosk.Name,
        Rid(kiosk.Platform),
        kiosk.UpdateChannel.ToString(),
        kiosk.AppVersion,
        kiosk.AgentVersion,
        kiosk.ProvisioningVersion,
        [
            .. kiosk.UpdateStates.OrderBy(state => state.Component).Select(state => new KioskUpdateStateView(
                state.Component.ToString(),
                state.Version,
                state.Status.ToString(),
                state.MessageKey is { } key ? Messages.Write(new UserMessage(key)) : null,
                state.ReportedAt)),
        ],
        [.. offers.Select(release => new KioskOfferView(release.Component.ToString(), release.Version))],
        kiosk.LastHeartbeatAt);

    private static string? InstalledVersion(Kiosk kiosk, ReleaseComponent component) => component switch
    {
        ReleaseComponent.App => kiosk.AppVersion,
        ReleaseComponent.Agent => kiosk.AgentVersion,
        ReleaseComponent.Provisioning => kiosk.ProvisioningVersion,
        _ => null,
    };

    private static string Describe(ReleaseRollout rollout) => rollout.Target switch
    {
        RolloutTarget.Kiosks => $"{rollout.KioskIds.Count} kiosks",
        RolloutTarget.Percent => $"{rollout.Percent}%",
        RolloutTarget.Channel => $"channel {rollout.Channel}",
        _ => "all",
    };
}

/// <summary>The package was larger than the manifest says (or than the limit).</summary>
public sealed class PackageTooLargeException : IOException
{
    public PackageTooLargeException()
        : base("The package is larger than its manifest says.")
    {
    }
}

/// <summary>
/// Passes a stream through, counting and hashing it, and stops it at a limit:
/// an upload is checked against its manifest while it is stored, without a
/// second copy on disk.
/// </summary>
internal sealed class CountingHashStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _sha256;

    public CountingHashStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    public override long Length => Count;

    /// <summary>Bytes read so far.</summary>
    public long Count { get; private set; }

    /// <summary>The SHA-256 of everything read, once the stream has been read to its end.</summary>
    public string Sha256 => _sha256 ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, _inner.Read(buffer, offset, count)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Take(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Take(ReadOnlySpan<byte> read)
    {
        Count += read.Length;
        if (Count > _limit)
        {
            throw new PackageTooLargeException();
        }

        _hash.AppendData(read);
        return read.Length;
    }
}
