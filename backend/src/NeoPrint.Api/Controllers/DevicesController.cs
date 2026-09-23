using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The endpoints a kiosk device uses before and during activation
/// (plan sections 6, 7.11).
/// </summary>
/// <remarks>
/// The kiosk build is universal: it ships with no credentials, so registration
/// has to be anonymous. It is rate limited per IP, the number of pending
/// devices is capped, and nothing here exposes any configuration until an
/// administrator has activated the device.
/// </remarks>
[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public DevicesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Registers a device on first start. With a valid enrollment token the
    /// device activates immediately; otherwise it gets a pairing code to show.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("device-register")]
    [ProducesResponseType(typeof(RegisterDeviceResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterDeviceResult>> Register(
        [FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new RegisterDeviceCommand(
                request.PublicKey,
                request.Fingerprint,
                request.Platform,
                request.AppVersion,
                request.AgentVersion,
                request.ProvisioningVersion,
                request.EnrollmentToken,
                request.Printers,
                _currentUser.Ip),
            cancellationToken));

    /// <summary>Issues a challenge for the device to sign.</summary>
    [HttpPost("challenge")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<DeviceChallengeResult>> Challenge(
        [FromBody] DeviceChallengeRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CreateDeviceChallengeCommand(request.DeviceId), cancellationToken));

    /// <summary>
    /// Exchanges a signed challenge for a token: a kiosk token once activated,
    /// a limited provisioning token before that.
    /// </summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<DeviceTokenResult>> Token(
        [FromBody] DeviceTokenRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CreateDeviceTokenCommand(
                request.DeviceId, request.Nonce, request.Signature, _currentUser.Ip),
            cancellationToken));

    /// <summary>Renews the pairing code shown on the activation screen.</summary>
    [HttpPost("me/pairing-code")]
    [Authorize(Policy = AuthorizationPolicies.Provisioning)]
    public async Task<ActionResult<PairingCodeResult>> RenewPairingCode(CancellationToken cancellationToken)
    {
        var deviceId = _currentUser.DeviceId;
        if (deviceId is null)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(new RenewPairingCodeCommand(deviceId.Value), cancellationToken));
    }

    /// <summary>
    /// Reports the printers the device can see, so an administrator can pick
    /// one while activating it.
    /// </summary>
    [HttpPut("me/printers")]
    [Authorize(Policy = AuthorizationPolicies.Device)]
    public async Task<IActionResult> ReportPrinters(
        [FromBody] ReportPrintersRequest request, CancellationToken cancellationToken)
    {
        var deviceId = _currentUser.DeviceId;
        if (deviceId is null)
        {
            return Forbid();
        }

        await _sender.Send(
            new ReportDevicePrintersCommand(deviceId.Value, request.Printers), cancellationToken);

        return NoContent();
    }

    public sealed record RegisterDeviceRequest(
        string PublicKey,
        DeviceFingerprintDto Fingerprint,
        KioskPlatform Platform,
        string? AppVersion,
        string? AgentVersion,
        string? ProvisioningVersion,
        string? EnrollmentToken,
        IReadOnlyCollection<ReportedPrinterDto>? Printers);

    public sealed record DeviceChallengeRequest(Guid DeviceId);

    public sealed record DeviceTokenRequest(Guid DeviceId, string Nonce, string Signature);

    public sealed record ReportPrintersRequest(IReadOnlyCollection<ReportedPrinterDto> Printers);
}
