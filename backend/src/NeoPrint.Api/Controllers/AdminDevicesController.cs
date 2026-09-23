using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Devices;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Devices;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Device activation from the admin and operator panels
/// (plan sections 6, 7.11, 8A.3).
/// </summary>
[ApiController]
[Route("api/admin/devices")]
public sealed class AdminDevicesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly ICurrentUser _currentUser;

    public AdminDevicesController(
        ISender sender,
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        ICurrentUser currentUser)
    {
        _sender = sender;
        _devices = devices;
        _kiosks = kiosks;
        _currentUser = currentUser;
    }

    /// <summary>Devices, optionally filtered by status.</summary>
    [HttpGet]
    [HasPermission(Permissions.DevicesView)]
    public async Task<ActionResult<IReadOnlyList<DeviceListItem>>> List(
        [FromQuery] DeviceStatus? status, CancellationToken cancellationToken)
    {
        var devices = await _devices.ListAsync(status, cancellationToken);
        var kiosks = (await _kiosks.ListAsync(cancellationToken)).ToDictionary(kiosk => kiosk.Id);

        return Ok(devices.Select(device => new DeviceListItem(
            device.Id,
            device.Status,
            device.Platform,
            device.AppVersion,
            device.AgentVersion,
            device.Fingerprint.Hostname,
            device.Fingerprint.Os,
            device.LastIp,
            device.CreatedAt,
            device.LastSeenAt,
            device.KioskId,
            device.KioskId is not null && kiosks.TryGetValue(device.KioskId.Value, out var kiosk)
                ? kiosk.Code
                : null,
            // The code itself is only ever hashed; the panel shows whether one
            // is still valid, and the operator reads the code off the kiosk.
            device.IsPairingCodeValid(DateTime.UtcNow),
            device.PairingCodeExpiresAt,
            [.. device.ReportedPrinters.Select(printer => printer.Name)])).ToList());
    }

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
            !await IsInScopeAsync(request.KioskId.Value, cancellationToken))
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
    public async Task<IActionResult> Revoke(
        Guid id, [FromBody] RevokeRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        var device = await _devices.FindByIdAsync(id, cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        if (device.KioskId is not null && !await IsInScopeAsync(device.KioskId.Value, cancellationToken))
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
    public async Task<ActionResult<EnrollmentTokenResult>> CreateEnrollmentToken(
        [FromBody] CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return Forbid();
        }

        if (request.KioskId is not null && !await IsInScopeAsync(request.KioskId.Value, cancellationToken))
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
        [FromServices] IEnrollmentTokenRepository tokens, CancellationToken cancellationToken)
    {
        var all = await tokens.ListAsync(cancellationToken);
        var kiosks = (await _kiosks.ListAsync(cancellationToken)).ToDictionary(kiosk => kiosk.Id);

        return Ok(all.Select(token => new EnrollmentTokenListItem(
            token.Id,
            token.KioskId,
            token.KioskId is not null && kiosks.TryGetValue(token.KioskId.Value, out var kiosk) ? kiosk.Code : null,
            token.NewKioskTemplate?.Name,
            token.CreatedAt,
            token.ExpiresAt,
            token.UsedByDeviceId,
            token.RevokedAt,
            token.IsUsable(DateTime.UtcNow))).ToList());
    }

    /// <summary>Revokes an unused enrollment token.</summary>
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
    /// Whether the caller's kiosk scope covers a kiosk (plan section 8A.2).
    /// </summary>
    private async Task<bool> IsInScopeAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var adminId = _currentUser.AdminUserId;
        if (adminId is null)
        {
            return false;
        }

        var resolver = HttpContext.RequestServices.GetRequiredService<IPermissionResolver>();
        var effective = await resolver.ResolveAsync(adminId.Value, cancellationToken);

        return effective.CanSee(kioskId);
    }

    public sealed record DeviceListItem(
        Guid Id,
        DeviceStatus Status,
        Domain.Kiosks.KioskPlatform Platform,
        string? AppVersion,
        string? AgentVersion,
        string? Hostname,
        string? Os,
        string? LastIp,
        DateTime RegisteredAt,
        DateTime LastSeenAt,
        Guid? KioskId,
        string? KioskCode,
        bool HasValidPairingCode,
        DateTime? PairingCodeExpiresAt,
        IReadOnlyCollection<string> ReportedPrinters);

    public sealed record LookupRequest(string CodeOrQr);

    public sealed record ActivateRequest(
        Guid? KioskId, NewKioskParameters? NewKiosk, string? SelectedPrinterName);

    public sealed record RevokeRequest(string? Reason);

    public sealed record CreateEnrollmentTokenRequest(
        Guid? KioskId, NewKioskParameters? NewKiosk, int? TtlHours);

    public sealed record EnrollmentTokenListItem(
        Guid Id,
        Guid? KioskId,
        string? KioskCode,
        string? NewKioskName,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        Guid? UsedByDeviceId,
        DateTime? RevokedAt,
        bool IsUsable);
}
