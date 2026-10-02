using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Incidents;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Incidents in the admin panel (plan section 6, "Admin Incidents"): the list
/// only, for the "Fiskal çeklər" page. Acknowledge and resolve come with step 7.
/// </summary>
[ApiController]
[Route("api/admin/incidents")]
public sealed class AdminIncidentsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminIncidentsController(ISender sender) => _sender = sender;

    /// <summary>Critical first, then the newest.</summary>
    [HttpGet]
    [HasPermission(Permissions.IncidentsView)]
    public async Task<ActionResult<IncidentPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] string[]? type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListIncidentsQuery(status, type, page, pageSize), cancellationToken));
}
