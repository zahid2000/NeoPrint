using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Features;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Feature flags (plan sections 2.16, 6).
/// </summary>
[ApiController]
[Route("api")]
public sealed class FeaturesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public FeaturesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The flags a mobile client should apply. Available anonymously so the app
    /// can render its first screen before anyone signs in; a percentage rollout
    /// only resolves once there is a user.
    /// </summary>
    [HttpGet("features")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyDictionary<string, bool>>> Mobile(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetMobileFeaturesQuery(_currentUser.UserId), cancellationToken));

    /// <summary>Every flag with its configuration, for the admin panel.</summary>
    [HttpGet("admin/features")]
    [HasPermission(Permissions.FeaturesManage)]
    public async Task<ActionResult<IReadOnlyList<FeatureFlagDetails>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListFeatureFlagsQuery(), cancellationToken));

    /// <summary>Updates a flag: default, rollout percentage, kiosk overrides, allowlist.</summary>
    [HttpPut("admin/features/{key}")]
    [HasPermission(Permissions.FeaturesManage)]
    public async Task<ActionResult<FeatureFlagDetails>> Update(
        string key, [FromBody] UpdateFeatureFlagRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(
            new UpdateFeatureFlagCommand(
                key,
                request.DefaultEnabled,
                request.RolloutPercent,
                request.KioskOverrides,
                request.UserAllowlist,
                adminId.Value,
                _currentUser.Ip),
            cancellationToken));
    }

    public sealed record UpdateFeatureFlagRequest(
        bool DefaultEnabled,
        int RolloutPercent,
        IReadOnlyDictionary<string, bool>? KioskOverrides,
        IReadOnlyCollection<Guid>? UserAllowlist);
}
