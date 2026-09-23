using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Devices;

namespace NeoPrint.Infrastructure.Devices;

/// <summary>
/// Confirms a device token against the device's current state
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// MongoDB is the source of truth here, not the Redis blocklist. The blocklist
/// is a fast path that saves a read in the common case, but a flushed or
/// unavailable Redis must not bring a revoked device back to life - which is
/// exactly what relying on it alone would do
/// (<c>docs/ARCHITECTURE_RULES.md</c> section 2).
/// </remarks>
public sealed class DeviceAccessChecker : IDeviceAccessChecker
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskTokenBlocklist _blocklist;
    private readonly ILogger<DeviceAccessChecker> _logger;

    public DeviceAccessChecker(
        IKioskDeviceRepository devices,
        IKioskTokenBlocklist blocklist,
        ILogger<DeviceAccessChecker> logger)
    {
        _devices = devices;
        _blocklist = blocklist;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceAccessResult> CheckAsync(
        Guid deviceId,
        string tokenType,
        Guid? kioskId,
        CancellationToken cancellationToken = default)
    {
        if (await IsBlockedAsync(deviceId, cancellationToken).ConfigureAwait(false))
        {
            return Refuse(deviceId, "the device is on the token blocklist");
        }

        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (device is null)
        {
            // The token names a device that no longer exists; a valid signature
            // over a deleted device is still not an identity.
            return Refuse(deviceId, "the device no longer exists");
        }

        return tokenType switch
        {
            TokenTypes.Kiosk => CheckKioskToken(device, kioskId),
            TokenTypes.Provisioning => CheckProvisioningToken(device),

            // Any other token type has no business carrying a device id.
            _ => Refuse(deviceId, $"token type '{tokenType}' cannot act as a device"),
        };
    }

    /// <summary>
    /// A kiosk token is only good while the device is active and still bound to
    /// the kiosk the token was issued for.
    /// </summary>
    private DeviceAccessResult CheckKioskToken(KioskDevice device, Guid? kioskId)
    {
        if (device.Status != DeviceStatus.Active)
        {
            return Refuse(device.Id, $"the device is {device.Status}, not Active");
        }

        if (device.KioskId is null)
        {
            return Refuse(device.Id, "the device is not bound to a kiosk");
        }

        if (kioskId is not null && device.KioskId != kioskId)
        {
            // The device was moved to another kiosk after this token was issued.
            // Serving it would let one kiosk act as another.
            return Refuse(device.Id, "the token was issued for a different kiosk");
        }

        return DeviceAccessResult.Allowed;
    }

    /// <summary>
    /// A provisioning token belongs to a device that is still waiting to be
    /// activated. An active device has a kiosk token instead, and a revoked or
    /// expired one has no way back in.
    /// </summary>
    private DeviceAccessResult CheckProvisioningToken(KioskDevice device)
        => device.Status is DeviceStatus.Pending or DeviceStatus.Active
            ? DeviceAccessResult.Allowed
            : Refuse(device.Id, $"the device is {device.Status}");

    private async Task<bool> IsBlockedAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        try
        {
            return await _blocklist.IsBlockedAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Redis being down must not open the door, and must not close it
            // either: the database check below is the one that decides.
            _logger.LogWarning(exception, "The kiosk token blocklist could not be read; falling back to the database.");
            return false;
        }
    }

    private DeviceAccessResult Refuse(Guid deviceId, string reason)
    {
        _logger.LogWarning("Device {DeviceId} was refused: {Reason}.", deviceId, reason);

        return DeviceAccessResult.Refused(reason);
    }
}
