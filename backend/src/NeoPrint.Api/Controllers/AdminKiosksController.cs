using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Kiosks;

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
    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;
    private readonly ICurrentUser _currentUser;

    public AdminKiosksController(
        IKioskRepository kiosks,
        IPermissionResolver permissions,
        ICurrentUser currentUser)
    {
        _kiosks = kiosks;
        _permissions = permissions;
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

        var effective = await _permissions.ResolveAsync(adminId.Value, cancellationToken);
        var kiosks = await _kiosks.ListAsync(cancellationToken);

        // A scoped operator sees only their own kiosks, so they cannot bind a
        // device to someone else's kiosk by picking it out of a list.
        return Ok(kiosks
            .Where(kiosk => effective.CanSee(kiosk.Id))
            .OrderBy(kiosk => kiosk.Code, StringComparer.Ordinal)
            .Select(kiosk => new KioskListItem(
                kiosk.Id,
                kiosk.Code,
                kiosk.Name,
                kiosk.Status,
                kiosk.Platform,
                kiosk.Location.Address,
                kiosk.DefaultLanguage,
                kiosk.ActiveDeviceId,
                kiosk.AppVersion,
                kiosk.SelectedPrinter?.Name,
                kiosk.LastHeartbeatAt))
            .ToList());
    }

    public sealed record KioskListItem(
        Guid Id,
        string Code,
        string Name,
        KioskStatus Status,
        KioskPlatform Platform,
        string Address,
        string DefaultLanguage,
        Guid? ActiveDeviceId,
        string? AppVersion,
        string? SelectedPrinter,
        DateTime? LastHeartbeatAt);
}
