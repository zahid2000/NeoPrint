namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Whether a device token still represents a device that may call the API
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// A device token lives an hour, but detaching a device has to take effect at
/// once: the plan says a revoked device becomes invalid immediately, and
/// <c>docs/STATE_MACHINES.md</c> says a revoked device must never be accepted
/// again. So the claims in the token are only a fast path - every request
/// confirms them against the device's current state.
/// </remarks>
public interface IDeviceAccessChecker
{
    /// <summary>
    /// Confirms a device may act with the token it presented.
    /// </summary>
    /// <param name="deviceId">The device the token names.</param>
    /// <param name="tokenType">
    /// <see cref="TokenTypes.Kiosk"/> or <see cref="TokenTypes.Provisioning"/>:
    /// each is only valid in the matching device status.
    /// </param>
    /// <param name="kioskId">
    /// The kiosk a kiosk token was issued for, so a token cannot be used after
    /// the device has been moved to another kiosk.
    /// </param>
    Task<DeviceAccessResult> CheckAsync(
        Guid deviceId,
        string tokenType,
        Guid? kioskId,
        CancellationToken cancellationToken = default);
}

/// <param name="IsAllowed">Whether the request may proceed.</param>
/// <param name="Reason">
/// Why it was refused, for the log. Never returned to the caller: a device
/// learning why it was refused learns nothing useful, and a detached kiosk
/// simply re-registers.
/// </param>
public sealed record DeviceAccessResult(bool IsAllowed, string? Reason = null)
{
    public static readonly DeviceAccessResult Allowed = new(true);

    public static DeviceAccessResult Refused(string reason) => new(false, reason);
}
