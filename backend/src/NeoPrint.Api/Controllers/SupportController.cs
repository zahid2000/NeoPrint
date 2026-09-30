using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Support;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// An anonymous customer's claim (plan section 2.5, "Müştəri müraciəti"): the
/// job code and the kiosk code from the receipt or the kiosk's screen. Limited
/// per IP, so the pair cannot be guessed at speed.
/// </summary>
[ApiController]
[Route("api/support/claims")]
[AllowAnonymous]
public sealed class SupportClaimsController : ControllerBase
{
    private readonly ISender _sender;

    public SupportClaimsController(ISender sender) => _sender = sender;

    [HttpPost]
    [EnableRateLimiting("support-claim")]
    [Idempotent]
    public async Task<ActionResult<SupportClaimReceipt>> Open([FromBody] AnonymousClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new OpenAnonymousClaimCommand(request.JobCode, request.KioskCode, request.Text, request.Contact), cancellationToken));
    }
}

public sealed record AnonymousClaimRequest(string? JobCode, string? KioskCode, string? Text, string? Contact);

/// <summary>"Problem barədə bildir" in the app (plan section 7A, support).</summary>
[ApiController]
[Route("api/me/claims")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class MyClaimsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public MyClaimsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SupportClaimView>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListMyClaimsQuery(UserId), cancellationToken));

    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<SupportClaimView>> Open([FromBody] AppClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new OpenAppClaimCommand(UserId, request.JobId, request.Text), cancellationToken));
    }
}

public sealed record AppClaimRequest(Guid JobId, string? Text);

/// <summary>Claims in the admin panel; they also show on the job's investigation card.</summary>
[ApiController]
[Route("api/admin/support/claims")]
public sealed class AdminClaimsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminClaimsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet]
    [HasPermission(Permissions.JobsView)]
    public async Task<ActionResult<SupportClaimPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] Guid? jobId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListClaimsQuery(status, jobId, page, pageSize), cancellationToken));

    [HttpPost("{id:guid}/answer")]
    [Idempotent]
    [HasPermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<SupportClaimView>> Answer(Guid id, [FromBody] AnswerClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new AnswerClaimCommand(AdminId, id, request.Reply, Ip), cancellationToken));
    }

    [HttpPost("{id:guid}/close")]
    [Idempotent]
    [HasPermission(Permissions.PaymentsRefund)]
    public async Task<ActionResult<SupportClaimView>> Close(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CloseClaimCommand(AdminId, id, Ip), cancellationToken));
}

public sealed record AnswerClaimRequest(string? Reply);
