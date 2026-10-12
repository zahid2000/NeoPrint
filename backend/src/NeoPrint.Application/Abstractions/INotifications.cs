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

    /// <summary>A sync job moved on: the storage wizard's live progress (plan section 2.10).</summary>
    Task StorageSyncProgressAsync(Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default);

    /// <summary>A migration or a mirror finished its copy: the wizard's last step.</summary>
    Task StorageSyncCompletedAsync(Storage.StorageSyncProgress progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// A kiosk's status changed - its printer stopped or returned, its
    /// heartbeat was lost or came back, an administrator paused it, a device
    /// was bound to it (plan section 9.6): the lists and status cards follow.
    /// Sent by the kiosk repository as the change is saved, not by handlers.
    /// </summary>
    Task KioskStatusChangedAsync(Guid kioskId, string status, CancellationToken cancellationToken = default);

    /// <summary>A kiosk reported a different list of printers (plan section 7.7).</summary>
    Task KioskPrintersUpdatedAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <summary>A kiosk sent a new report of its state (plan section 7.9): the kiosk detail updates live.</summary>
    Task KioskTelemetryUpdatedAsync(Kiosks.KioskTelemetryView telemetry, CancellationToken cancellationToken = default);

    /// <summary>A kiosk sent new readings of its printers (plan section 7.10): the printer detail updates live.</summary>
    Task PrinterTelemetryUpdatedAsync(
        Guid kioskId, IReadOnlyList<Kiosks.PrinterTelemetryView> printers, CancellationToken cancellationToken = default);

    /// <summary>
    /// An account's kiosk scope or roles changed, or it was switched off or
    /// given a new password: its open panels are cut off and connect again,
    /// which is when the kiosks they may hear about are worked out (T-249).
    /// </summary>
    Task AccountChangedAsync(Guid adminUserId, CancellationToken cancellationToken = default);

    /// <summary>A kiosk answered a command (plan section 7.8).</summary>
    Task CommandResultAsync(Guid kioskId, Kiosks.KioskCommandView command, CancellationToken cancellationToken = default);

    /// <summary>A kiosk's queue changed (plan section 8A.3, kiosk detail → Queue).</summary>
    Task QueueUpdatedAsync(
        Guid kioskId, IReadOnlyList<PrintJobs.QueueEntry> queue, CancellationToken cancellationToken = default);

    /// <summary>A provider's health changed (every-minute check, plan section 2.10).</summary>
    Task StorageHealthChangedAsync(Storage.StorageHealthChanged change, CancellationToken cancellationToken = default);
}
