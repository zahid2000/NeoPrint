using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Printerlər (ümumi)" (plan section 8A.3): the printers of every kiosk the
/// caller sees, each as its kiosk last reported it (section 7.10). A printer
/// of one kiosk is read and acted on under <c>admin/kiosks/{id}/printers</c>.
/// </summary>
[ApiController]
[Route("api/admin/printers")]
public sealed class AdminPrintersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPrintersController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    /// <summary>The latest reading of every printer in the caller's scope; a printer not heard of for a week is left out.</summary>
    [HttpGet]
    [HasPermission(Permissions.PrintersView)]
    public async Task<ActionResult<IReadOnlyList<PrinterTelemetryView>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetAllPrinterTelemetryQuery(AdminId), cancellationToken));
}
