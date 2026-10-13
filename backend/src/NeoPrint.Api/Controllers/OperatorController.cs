using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Operator;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The operator panel's own two screens (plan section 8B): "Mənim kiosklarım"
/// and the kiosk's short detail. Everything else the panel does - a refill, a
/// task, an incident, the warehouse - is the endpoints the admin panel uses,
/// with the same permissions.
/// </summary>
/// <remarks>
/// Both answers are cut to the caller's kiosk scope, and a part of a card the
/// caller may not read is left empty.
/// </remarks>
[ApiController]
[Route("api/admin/operator")]
[HasPermission(Permissions.OperatorPanel)]
public sealed class OperatorController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public OperatorController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>One health card a kiosk, the ones that need somebody first.</summary>
    [HttpGet("kiosks")]
    public async Task<ActionResult<IReadOnlyList<OperatorKioskCard>>> Kiosks(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListOperatorKiosksQuery(AdminId), cancellationToken));

    /// <summary>The card, what the machine last reported, and the incidents and tasks open for it.</summary>
    [HttpGet("kiosks/{id:guid}")]
    public async Task<ActionResult<OperatorKioskDetail>> Kiosk(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetOperatorKioskQuery(AdminId, id), cancellationToken));
}
