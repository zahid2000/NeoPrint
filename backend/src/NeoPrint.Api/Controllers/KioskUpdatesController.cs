using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Releases;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Releases;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Updates from the kiosk's side (plan section 6 "Agent (Kiosk token)",
/// 7.12; T-317): what to run, the package itself, and how the update went.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class KioskUpdatesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public KioskUpdatesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// What this kiosk should run, component by component, with the signed
    /// manifest and signature to check each package against, and the packages
    /// withdrawn since their release.
    /// </summary>
    [HttpGet("kiosks/me/update")]
    public async Task<ActionResult<KioskUpdateView>> Update(CancellationToken cancellationToken)
        => _currentUser.DeviceId is { } deviceId
            ? Ok(await _sender.Send(new GetKioskUpdateQuery(deviceId), cancellationToken))
            : Forbid();

    /// <summary>A package this kiosk is offered right now; any other is "not found".</summary>
    [HttpGet("releases/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        var download = await _sender.Send(new OpenReleasePackageQuery(deviceId, id), cancellationToken);
        return File(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }

    /// <summary>Where an update of one component stands on this kiosk.</summary>
    [HttpPost("kiosks/me/update-status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(
            new ReportKioskUpdateCommand(deviceId, request.Component, request.Version ?? string.Empty, request.Status, request.MessageKey, _currentUser.Ip),
            cancellationToken);
        return NoContent();
    }
}

/// <param name="MessageKey">What happened, as a message key the backend knows (<c>updates.signatureRefused</c>).</param>
public sealed record UpdateStatusRequest(ReleaseComponent Component, string? Version, KioskUpdateStatus Status, string? MessageKey);
