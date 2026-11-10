using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The kiosk's admin menu opened by an operator's QR (T-392): the kiosk asks
/// for a QR, looks whether an operator confirmed it, and sends the operator's
/// changes - carried out under the operator's account and audited in its name.
/// </summary>
[ApiController]
[Route("api/kiosks/me/operator-logins")]
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class KioskOperatorLoginController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public KioskOperatorLoginController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpPost]
    public async Task<ActionResult<OperatorKioskLoginStarted>> Start(CancellationToken cancellationToken)
        => _currentUser.DeviceId is { } deviceId
            ? Ok(await _sender.Send(new StartOperatorKioskLoginCommand(deviceId), cancellationToken))
            : Forbid();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OperatorKioskLoginView>> Get(Guid id, CancellationToken cancellationToken)
        => _currentUser.DeviceId is { } deviceId
            ? Ok(await _sender.Send(new GetOperatorKioskLoginQuery(deviceId, id), cancellationToken))
            : Forbid();

    /// <summary>{ "action": "SelectPrinter" | "SetQueueLimit" | "Pause" | "Resume", "printer"?, "limit"? }</summary>
    [HttpPost("{id:guid}/actions")]
    public async Task<IActionResult> Act(Guid id, [FromBody] OperatorKioskActionRequest body, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(new OperatorKioskActionCommand(deviceId, id, body.Action, body.Printer, body.Limit, _currentUser.Ip), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(new EndOperatorKioskLoginCommand(deviceId, id), cancellationToken);
        return NoContent();
    }
}

public sealed record OperatorKioskActionRequest(OperatorKioskAction Action, string? Printer, int? Limit);
