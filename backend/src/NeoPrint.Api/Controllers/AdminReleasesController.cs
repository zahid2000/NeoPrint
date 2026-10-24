using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Releases;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Releases;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Releases and rollouts (plan section 6 "Admin Releases", 8A "Buraxılışlar";
/// T-317). Global resources: an account that looks after some kiosks sees
/// none of it (<see cref="Permissions.GlobalOnly"/>).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminReleasesController : ControllerBase
{
    /// <summary>The largest request: a package of <see cref="ReleaseSettings.MaxPackageMb"/> and two small files.</summary>
    private const long MaxRequestBytes = 260L * 1024 * 1024;

    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminReleasesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>The releases page: the key set this server takes, the releases and the rollouts.</summary>
    [HttpGet("releases")]
    [HasPermission(Permissions.ReleasesView)]
    public async Task<ActionResult<ReleasesPageView>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListReleasesQuery(), cancellationToken));

    /// <summary>
    /// Uploads a signed package: the package, its <c>manifest.json</c> and the
    /// manifest's <c>.sig</c> (docs/RELEASE_SIGNING.md). Checked as a kiosk will
    /// check it; a package that is not signed for this server's key set is refused.
    /// </summary>
    /// <remarks>
    /// The form is read here rather than bound as parameters: bound form
    /// parameters make the endpoint refuse another content type (415) before
    /// the caller is even asked who they are - an anonymous caller would learn
    /// the endpoint's shape. Here the permission is checked first.
    /// </remarks>
    [HttpPost("releases")]
    [HasPermission(Permissions.ReleasesManage)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<ActionResult<ReleaseView>> Upload(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return ValidationProblem("package", "releases.packageMissing");
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var package = form.Files.GetFile("package");
        var manifest = form.Files.GetFile("manifest");
        var signature = form.Files.GetFile("signature");
        var channel = Enum.TryParse<ReleaseChannel>(form["channel"], ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : ReleaseChannel.Pilot;

        if (package is null || package.Length == 0)
        {
            return ValidationProblem("package", "releases.packageMissing");
        }

        if (manifest is null || manifest.Length is 0 or > ReleaseHandlers.MaxSmallFile)
        {
            return ValidationProblem("manifest", "releases.manifestMissing");
        }

        if (signature is null || signature.Length is 0 or > ReleaseHandlers.MaxSmallFile)
        {
            return ValidationProblem("signature", "releases.signatureMissing");
        }

        await using var content = package.OpenReadStream();
        var view = await _sender.Send(
            new UploadReleaseCommand(
                AdminId,
                content,
                package.FileName,
                await ReadAsync(manifest, cancellationToken),
                await ReadAsync(signature, cancellationToken),
                channel,
                _currentUser.Ip),
            cancellationToken);

        return Ok(view);
    }

    /// <summary>From the pilot kiosks to everyone.</summary>
    [HttpPost("releases/{id:guid}/promote")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<ReleaseView>> Promote(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new PromoteReleaseCommand(AdminId, id, _currentUser.Ip), cancellationToken));

    /// <summary>No kiosk installs the package any more; its rollouts are taken back.</summary>
    [HttpPost("releases/{id:guid}/withdraw")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<ReleaseView>> Withdraw(Guid id, [FromBody] WithdrawReleaseRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new WithdrawReleaseCommand(AdminId, id, request?.Reason ?? string.Empty, _currentUser.Ip), cancellationToken));

    [HttpPost("rollouts")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<RolloutView>> CreateRollout([FromBody] CreateRolloutRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ValidationProblem("target", "rollouts.targetUnknown");
        }

        return Ok(await _sender.Send(
            new CreateRolloutCommand(AdminId, request.ReleaseId, request.Target, request.KioskIds, request.Percent, request.Channel, _currentUser.Ip, request.Urgent),
            cancellationToken));
    }

    [HttpPost("rollouts/{id:guid}/pause")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<RolloutView>> Pause(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ChangeRolloutCommand(AdminId, id, RolloutAction.Pause, _currentUser.Ip), cancellationToken));

    [HttpPost("rollouts/{id:guid}/resume")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<RolloutView>> Resume(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ChangeRolloutCommand(AdminId, id, RolloutAction.Resume, _currentUser.Ip), cancellationToken));

    [HttpPost("rollouts/{id:guid}/complete")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<RolloutView>> Complete(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ChangeRolloutCommand(AdminId, id, RolloutAction.Complete, _currentUser.Ip), cancellationToken));

    /// <summary>Takes a rollout back: the kiosks it reached are offered the release before it again.</summary>
    [HttpPost("rollouts/{id:guid}/rollback")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<RolloutView>> RollBack(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ChangeRolloutCommand(AdminId, id, RolloutAction.RollBack, _currentUser.Ip), cancellationToken));

    /// <summary>Every kiosk with its UI, agent and provisioning versions and update state (plan section 6, <c>admin/kiosks/versions</c>).</summary>
    [HttpGet("kiosks/versions")]
    [HasPermission(Permissions.ReleasesView)]
    public async Task<ActionResult<IReadOnlyList<KioskVersionView>>> Versions(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListKioskVersionsQuery(AdminId), cancellationToken));

    /// <summary>Which releases reach a kiosk first: pilot or stable.</summary>
    [HttpPut("kiosks/{id:guid}/update-channel")]
    [HasPermission(Permissions.ReleasesManage)]
    public async Task<ActionResult<KioskVersionView>> SetUpdateChannel(Guid id, [FromBody] UpdateChannelRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetKioskUpdateChannelCommand(AdminId, id, request?.Channel ?? ReleaseChannel.Stable, _currentUser.Ip), cancellationToken));

    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private ActionResult ValidationProblem(string field, string key)
        => base.ValidationProblem(new ValidationProblemDetails(
            new Dictionary<string, string[]> { [field] = [Messages.Write(new UserMessage(key))] })
        {
            Title = Messages.Write(new UserMessage("problem.validation")),
        });
}

public sealed record WithdrawReleaseRequest(string? Reason);

/// <param name="KioskIds">For <see cref="RolloutTarget.Kiosks"/>.</param>
/// <param name="Percent">For <see cref="RolloutTarget.Percent"/>, 1-100.</param>
/// <param name="Channel">For <see cref="RolloutTarget.Channel"/>.</param>
public sealed record CreateRolloutRequest(
    Guid ReleaseId,
    RolloutTarget Target,
    IReadOnlyCollection<Guid>? KioskIds,
    int? Percent,
    ReleaseChannel? Channel,
    bool Urgent = false);

public sealed record UpdateChannelRequest(ReleaseChannel Channel);
