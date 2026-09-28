using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Application.PrintJobs;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// What a kiosk asks the backend for once it is activated
/// (plan sections 6, 7.9, 7.11).
/// </summary>
/// <remarks>
/// The kiosk application is a universal build, so everything that makes it
/// this kiosk comes from here. Every endpoint needs a kiosk token, which also
/// confirms the device is still active on each request.
/// </remarks>
[ApiController]
[Route("api/kiosks")]
public sealed class KiosksController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public KiosksController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// The kiosk's own configuration: printer, limits, paper, feature flags.
    /// </summary>
    /// <remarks>
    /// Fetched after activation and whenever a heartbeat reports that the
    /// configuration has changed. No backend secret is in it; the admin PIN
    /// travels as a hash (plan sections 7.11, 2.13).
    /// </remarks>
    [HttpGet("me/config")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<KioskConfigResponse>> Config(CancellationToken cancellationToken)
    {
        var deviceId = _currentUser.DeviceId;
        if (deviceId is null)
        {
            return Forbid();
        }

        var config = await _sender.Send(new GetKioskConfigQuery(deviceId.Value), cancellationToken);

        // A token whose device is no longer bound to a kiosk has nothing to
        // configure; the device returns to its activation screen.
        return config is null ? Forbid() : Ok(config);
    }

    /// <summary>The kiosk's queue in order: the panel on screen and the next job to print (plan sections 7.2, 7.4).</summary>
    [HttpGet("me/queue")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<IReadOnlyList<QueueEntry>>> Queue(CancellationToken cancellationToken)
    {
        var deviceId = _currentUser.DeviceId;
        if (deviceId is null)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(new GetKioskQueueQuery(deviceId.Value), cancellationToken));
    }

    /// <summary>
    /// Reports that the kiosk is alive (plan section 7.9).
    /// </summary>
    /// <remarks>
    /// Also how a kiosk comes back: one marked Offline because its heartbeat
    /// stopped returns to service on the next one, with no operator involved.
    /// </remarks>
    [HttpPost("heartbeat")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<KioskHeartbeatResult>> Heartbeat(
        [FromBody] HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var deviceId = _currentUser.DeviceId;
        if (deviceId is null)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(
            new KioskHeartbeatCommand(
                deviceId.Value,
                request.AppVersion,
                request.AgentVersion,
                request.PrinterOnline,
                _currentUser.Ip),
            cancellationToken));
    }

    public sealed record HeartbeatRequest(
        string? AppVersion, string? AgentVersion, bool PrinterOnline);
}
