using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Reading kiosks (plan sections 6, 8A.3).
/// </summary>
/// <remarks>
/// Step 1 only needs the list: the activation wizard has to offer the existing
/// kiosks a device can be bound to. Creating, editing and deleting kiosks
/// arrive with the kiosk management screens in a later step.
/// </remarks>
[ApiController]
[Route("api/admin/kiosks")]
public sealed class AdminKiosksController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminKiosksController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>The kiosks the caller's scope covers (plan section 8A.2).</summary>
    [HttpGet]
    [HasPermission(Permissions.KiosksView)]
    public async Task<ActionResult<IReadOnlyList<KioskListItem>>> List(
        CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;

        if (adminId is null)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(new ListKiosksQuery(adminId.Value), cancellationToken));
    }
}
