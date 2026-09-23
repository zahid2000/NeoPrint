namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Real-time messages to a kiosk device over <c>/hubs/provisioning</c>
/// (plan section 6).
/// </summary>
public interface IDeviceNotifier
{
    /// <summary>
    /// Tells a device sitting on the activation screen that it has been
    /// activated, so it can fetch a kiosk token immediately instead of polling.
    /// </summary>
    Task DeviceActivatedAsync(Guid deviceId, Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells a device it has been detached, so it can wipe its cached
    /// configuration and spool and go back to the activation screen without
    /// waiting for its token to expire.
    /// </summary>
    Task DeviceRevokedAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Tells a device its pairing code was renewed.</summary>
    Task PairingCodeRenewedAsync(
        Guid deviceId, string pairingCode, DateTime expiresAt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Kiosk tokens that must stop being accepted before they expire.
/// </summary>
/// <remarks>
/// A kiosk token is valid for an hour, but revoking a device has to take effect
/// at once (plan section 7.11). Held in Redis with the token's remaining
/// lifetime, so entries clean themselves up.
/// </remarks>
public interface IKioskTokenBlocklist
{
    Task BlockAsync(Guid deviceId, TimeSpan duration, CancellationToken cancellationToken = default);

    Task<bool> IsBlockedAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real-time messages to the admin and operator panels over
/// <c>/hubs/admin</c> (plan section 6).
/// </summary>
public interface IAdminNotifier
{
    /// <summary>
    /// Tells open panels that a role changed, so they reload their own
    /// permissions rather than acting on a stale menu (plan section 8A.2).
    /// </summary>
    Task PermissionsChangedAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>A new device is waiting to be activated.</summary>
    Task DeviceRegisteredAsync(Guid deviceId, CancellationToken cancellationToken = default);

    Task DeviceActivatedAsync(Guid deviceId, Guid kioskId, CancellationToken cancellationToken = default);

    Task DeviceRevokedAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>A feature flag changed.</summary>
    Task FeatureFlagsChangedAsync(CancellationToken cancellationToken = default);
}
