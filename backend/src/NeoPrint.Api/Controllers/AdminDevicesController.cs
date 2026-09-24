using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Devices;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Device activation from the admin and operator panels
/// (plan sections 6, 7.11, 8A.3).
/// </summary>
/// <remarks>
/// Transport only: what a device may become, which kiosks an account may act
/// on and what activation does all live in the application layer
/// (<c>docs/ARCHITECTURE_RULES.md</c> section 1). The controller turns the
/// answers into status codes.
/// </remarks>
[ApiController]
[Route("api/admin/devices")]
public sealed class AdminDevicesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminDevicesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Devices, optionally filtered by status.</summary>
    [HttpGet]
    [HasPermission(Permissions.DevicesView)]
    public async Task<ActionResult<IReadOnlyList<DeviceListItem>>> List(
        [FromQuery] DeviceStatus? status, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListDevicesQuery(status), cancellationToken));

    /// <summary>
    /// Finds a device from a pairing code an admin typed, or from the QR an
    /// operator scanned off the kiosk screen.
    /// </summary>
    [HttpPost("lookup")]
    [HasPermission(Permissions.KiosksActivate)]
    public async Task<ActionResult<DeviceDetails>> Lookup(
        [FromBody] LookupRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new LookupDeviceCommand(request.CodeOrQr), cancellationToken));

    /// <summary>
    /// Activates a device, either onto an existing kiosk or as a new one.
    /// </summary>
    [HttpPost("{id:guid}/activate")]
    [HasPermission(Permissions.KiosksActivate)]
    [Idempotent]
    public async Task<ActionResult<ActivateDeviceResult>> Activate(
        Guid id, [FromBody] ActivateRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        // Binding to an existing kiosk must respect the caller's kiosk scope,
        // or a scoped operator could take over a kiosk that is not theirs.
        if (request.KioskId is not null &&
            !await IsInScopeAsync(adminId.Value, request.KioskId.Value, cancellationToken))
        {
            return Forbid();
        }

        var result = await _sender.Send(
            new ActivateDeviceCommand(
                id,
                request.KioskId,
                request.NewKiosk,
                request.SelectedPrinterName,
                adminId.Value,
                _currentUser.Ip),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Detaches a device. The kiosk keeps its code and configuration.</summary>
    [HttpPost("{id:guid}/revoke")]
    [HasPermission(Permissions.DevicesRevoke)]
    [Idempotent]
    public async Task<IActionResult> Revoke(
        Guid id, [FromBody] RevokeRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        var device = await _sender.Send(new FindDeviceQuery(id), cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        if (device.KioskId is not null &&
            !await IsInScopeAsync(adminId.Value, device.KioskId.Value, cancellationToken))
        {
            return Forbid();
        }

        await _sender.Send(
            new RevokeDeviceCommand(id, request.Reason, adminId.Value, _currentUser.Ip), cancellationToken);

        return NoContent();
    }

    /// <summary>Creates a single-use enrollment token for zero-touch activation.</summary>
    [HttpPost("/api/admin/enrollment-tokens")]
    [HasPermission(Permissions.DevicesEnroll)]
    [Idempotent]
    public async Task<ActionResult<EnrollmentTokenResult>> CreateEnrollmentToken(
        [FromBody] CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        if (request.KioskId is not null &&
            !await IsInScopeAsync(adminId.Value, request.KioskId.Value, cancellationToken))
        {
            return Forbid();
        }

        var result = await _sender.Send(
            new CreateEnrollmentTokenCommand(request.KioskId, request.NewKiosk, request.TtlHours, adminId.Value),
            cancellationToken);

        // The only time the raw token is ever returned; only its hash is stored.
        return Ok(result);
    }

    /// <summary>Enrollment tokens, without the tokens themselves.</summary>
    [HttpGet("/api/admin/enrollment-tokens")]
    [HasPermission(Permissions.DevicesEnroll)]
    public async Task<ActionResult<IReadOnlyList<EnrollmentTokenListItem>>> ListEnrollmentTokens(
        CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListEnrollmentTokensQuery(), cancellationToken));

    /// <summary>Revokes an enrollment token that has not been used.</summary>
    [HttpDelete("/api/admin/enrollment-tokens/{id:guid}")]
    [HasPermission(Permissions.DevicesEnroll)]
    public async Task<IActionResult> RevokeEnrollmentToken(Guid id, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        await _sender.Send(new RevokeEnrollmentTokenCommand(id, adminId.Value), cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Asks whether the caller's scope covers a kiosk.
    /// </summary>
    /// <remarks>
    /// What "in scope" means is decided in the application layer; the answer is
    /// turned into a status code here, which is the controller's job.
    /// </remarks>
    private Task<bool> IsInScopeAsync(Guid adminId, Guid kioskId, CancellationToken cancellationToken)
        => _sender.Send(new IsKioskInScopeQuery(adminId, kioskId), cancellationToken);

    public sealed record LookupRequest(string CodeOrQr);

    public sealed record ActivateRequest(
        Guid? KioskId, NewKioskParameters? NewKiosk, string? SelectedPrinterName);

    public sealed record RevokeRequest(string? Reason);

    public sealed record CreateEnrollmentTokenRequest(
        Guid? KioskId, NewKioskParameters? NewKiosk, int? TtlHours);
}
