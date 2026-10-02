using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Sessions;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The app joined to a kiosk, and the orders placed in it (plan sections 6
/// "Sessions" and 9.3).
/// </summary>
[ApiController]
[Route("api/sessions")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class SessionsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public SessionsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>Opens a session at a kiosk, by its id or code; the same kiosk again extends it.</summary>
    [HttpPost]
    public async Task<ActionResult<KioskSessionView>> Open([FromBody] OpenSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new OpenKioskSessionCommand(UserId, request.Kiosk ?? string.Empty), cancellationToken));
    }

    /// <summary>The user's open session; <c>204</c> when there is none.</summary>
    [HttpGet("current")]
    public async Task<ActionResult<KioskSessionView>> Current(CancellationToken cancellationToken)
        => await _sender.Send(new GetCurrentKioskSessionQuery(UserId), cancellationToken) is { } session
            ? Ok(session)
            : NoContent();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<KioskSessionView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskSessionQuery(UserId, id), cancellationToken));

    /// <summary>Ends it. Ending one that is already over is no error.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Close(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new CloseKioskSessionCommand(UserId, id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// An order for one of the user's files at the session's kiosk (plan
    /// section 9.3, step 2: <c>Source=MobileSession</c>). The answer carries the
    /// payment to pay it with, through <c>payments/{id}/pay-with-app</c>.
    /// </summary>
    /// <remarks>
    /// Under the session rather than at <c>POST print-jobs</c>: that route is the
    /// kiosk's and takes kiosk tokens only (docs/DECISIONS.md, 2026-10-03).
    /// Idempotent: a retry after a lost answer gets the same order and payment.
    /// </remarks>
    [HttpPost("{id:guid}/print-jobs")]
    [Idempotent]
    public async Task<ActionResult<SessionPrintJobResponse>> PlaceOrder(
        Guid id, [FromBody] SessionPrintJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new CreateSessionPrintJobCommand(UserId, id, request.FileId, request.Options ?? PrintOptions.Default),
            cancellationToken));
    }
}

/// <param name="Kiosk">The kiosk's id or code (<c>KSK-0001</c>, from the search or its QR).</param>
public sealed record OpenSessionRequest(string? Kiosk);

public sealed record SessionPrintJobRequest(Guid FileId, PrintOptions? Options);
