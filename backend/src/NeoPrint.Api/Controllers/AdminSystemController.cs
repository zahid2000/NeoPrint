using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Operations;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Arxa plan işləri" (plan section 6, T-215) and the way into the Hangfire
/// panel (T-167).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminSystemController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IWebHostEnvironment _environment;

    public AdminSystemController(ISender sender, ICurrentUser currentUser, IWebHostEnvironment environment)
    {
        _sender = sender;
        _currentUser = currentUser;
        _environment = environment;
    }

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    /// <summary>The last run of each check, and the kiosk events not kept. Every-kiosk accounts only (403 otherwise).</summary>
    [HttpGet("system/jobs")]
    [HasPermission(Permissions.SystemJobsView)]
    public async Task<ActionResult<SystemJobsView>> Jobs(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetSystemJobsQuery(AdminId), cancellationToken));

    /// <summary>
    /// A ticket for the Hangfire panel (a cookie for <c>/api/admin/hangfire</c>,
    /// 30 minutes), for a SuperAdmin; the opening is audited.
    /// </summary>
    [HttpPost("hangfire-session")]
    [HasPermission(Permissions.SystemJobsView)]
    public async Task<IActionResult> HangfireSession(CancellationToken cancellationToken)
    {
        if (!await _sender.Send(new OpenHangfireCommand(AdminId, _currentUser.Ip), cancellationToken))
        {
            throw new UnauthorizedAccessException("The Hangfire panel is for a SuperAdmin.");
        }

        // Secure everywhere but a developer's plain-http machine.
        HangfireDashboard.IssueTicket(HttpContext, AdminId, Request.IsHttps || !_environment.IsDevelopment());

        return NoContent();
    }
}
