using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Incidents;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Incidents in the admin and operator panels (plan section 6, "Admin
/// Incidents"; section 8A.3). An account with a kiosk scope sees and acts on
/// the incidents of its own kiosks only.
/// </summary>
[ApiController]
[Route("api/admin/incidents")]
public sealed class AdminIncidentsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminIncidentsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>Critical first, then the newest.</summary>
    [HttpGet]
    [HasPermission(Permissions.IncidentsView)]
    public async Task<ActionResult<IncidentPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] string[]? type,
        [FromQuery] Guid? kioskId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListIncidentsQuery(AdminId, status, type, kioskId, page, pageSize), cancellationToken));

    /// <summary>One incident with the orders, payments and refunds it touched (plan section 9.6, A.6).</summary>
    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.IncidentsView)]
    public async Task<ActionResult<IncidentDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetIncidentQuery(AdminId, id), cancellationToken));

    /// <summary>"I have seen it and am on it." Repeating it changes nothing.</summary>
    [HttpPost("{id:guid}/acknowledge")]
    [HasPermission(Permissions.IncidentsAcknowledge)]
    public async Task<ActionResult<IncidentView>> Acknowledge(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new AcknowledgeIncidentCommand(AdminId, id, _currentUser.Ip), cancellationToken));

    /// <summary>Closes the incident. The note is mandatory (plan section 8A.1) and goes to the audit log.</summary>
    [HttpPost("{id:guid}/resolve")]
    [HasPermission(Permissions.IncidentsResolve)]
    public async Task<ActionResult<IncidentView>> Resolve(
        Guid id, [FromBody] ResolveIncidentRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ResolveIncidentCommand(AdminId, id, request?.Note, _currentUser.Ip), cancellationToken));
}

public sealed record ResolveIncidentRequest(string? Note);

/// <summary>
/// What a kiosk reports that the backend cannot see for itself (plan section
/// 6, "Incidents": <c>POST incidents</c>, Kiosk role).
/// </summary>
/// <remarks>
/// The kiosk names a type and nothing else: it is an untrusted device, so no
/// text of its own is stored, and one open incident per kiosk and type is all
/// it can create however often it calls.
/// </remarks>
[ApiController]
[Route("api/incidents")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class IncidentsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public IncidentsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <param name="request"><c>type</c>: <c>PrinterOffline</c>, <c>PaperOut</c>, <c>PaperJam</c>, <c>SelectedPrinterMissing</c> or <c>AppCrash</c>.</param>
    [HttpPost]
    public async Task<IActionResult> Report([FromBody] KioskIncidentRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(new ReportKioskIncidentCommand(deviceId, request?.Type ?? string.Empty), cancellationToken);

        return NoContent();
    }
}

public sealed record KioskIncidentRequest(string? Type);
