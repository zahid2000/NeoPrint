using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Kiosks;
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

    /// <summary>The printers the kiosk can see: on start, when the list changes, on RefreshPrinters (plan section 7.7).</summary>
    [HttpPut("me/printers")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<IActionResult> Printers([FromBody] KioskPrintersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(new ReportKioskPrintersCommand(deviceId, request.Printers ?? []), cancellationToken);

        return NoContent();
    }

    /// <summary>Commands not answered yet: the ones pushed while the kiosk was offline (plan section 7.8).</summary>
    [HttpGet("me/commands")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<IReadOnlyList<KioskCommandView>>> Commands(CancellationToken cancellationToken)
        => _currentUser.DeviceId is { } deviceId
            ? Ok(await _sender.Send(new GetOpenKioskCommandsQuery(deviceId), cancellationToken))
            : Forbid();

    /// <summary>The kiosk's answer to a command; repeating it changes nothing.</summary>
    [HttpPost("me/commands/{id:guid}/result")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<KioskCommandView>> CommandResult(
        Guid id, [FromBody] KioskCommandResultRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(
            new CompleteKioskCommandCommand(deviceId, id, request.Succeeded, request.MessageKey, request.SheetsUsed),
            cancellationToken));
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
                _currentUser.Ip,
                request.SafeMode),
            cancellationToken));
    }

    /// <param name="SafeMode">The kiosk UI started in safe mode after a crash loop (plan section 7.12).</param>
    public sealed record HeartbeatRequest(
        string? AppVersion, string? AgentVersion, bool PrinterOnline, bool SafeMode = false);
}

public sealed record KioskPrintersRequest(IReadOnlyCollection<ReportedPrinterDto>? Printers);

public sealed record KioskCommandResultRequest(bool Succeeded, string? MessageKey, int SheetsUsed);
