using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Audit log" (plan section 8A.3): who did what to what, with what it was
/// before and after - read only. An account with a kiosk scope reads the
/// entries about its own kiosks.
/// </summary>
[ApiController]
[Route("api/admin/audit-logs")]
public sealed class AdminAuditController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminAuditController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    /// <summary>Newest first. <c>action</c> is a prefix: <c>kiosks.</c> is every kiosk action.</summary>
    [HttpGet]
    [HasPermission(Permissions.AuditView)]
    public async Task<ActionResult<AuditLogPage>> List(
        [FromQuery] string? action = null,
        [FromQuery] string? entityType = null,
        [FromQuery] string? entityId = null,
        [FromQuery] Guid? adminUserId = null,
        [FromQuery] string? actorType = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(
            new ListAuditLogsQuery(
                AdminId,
                new AuditLogFilter(action, entityType, entityId, adminUserId, actorType, from, to, correlationId),
                page,
                pageSize),
            cancellationToken));
}
