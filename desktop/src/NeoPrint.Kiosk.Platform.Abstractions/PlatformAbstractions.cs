namespace NeoPrint.Kiosk.Platform.Abstractions;

/// <summary>
/// Where the kiosk keeps its data (plan section 7.6).
/// </summary>
/// <remarks>
/// Everything lives outside the application folder, so an update, a restart or
/// a reinstall leaves the device identity, the cached configuration and the
/// spool untouched - which is what makes an update need no re-activation
/// (plan section 7.12).
/// </remarks>
public interface IAppPaths
{
    /// <summary>Device key pair and the cached configuration.</summary>
    string DeviceDirectory { get; }

    /// <summary>Encrypted spool for files waiting to print.</summary>
    string SpoolDirectory { get; }

    string LogDirectory { get; }

    /// <summary>Non-secret local state, such as the job journal.</summary>
    string StateDirectory { get; }

    /// <summary>Creates any directory that is missing, with restrictive permissions.</summary>
    void EnsureCreated();
}

/// <summary>
/// The device's own key pair (plan section 7.11).
/// </summary>
/// <remarks>
/// The private key never leaves the machine: on Windows it is a non-exportable
/// CNG key, held in the TPM where one is available; on Linux it is a file
/// readable only by the service account. The backend only ever sees the public
/// key, so nothing it stores can impersonate a kiosk.
/// </remarks>
public interface IDeviceKeyStore
{
    /// <summary>Whether this machine has already generated a key.</summary>
    bool Exists { get; }

    /// <summary>
    /// Returns the public key in SPKI form, base64, generating the pair on
    /// first use.
    /// </summary>
    string GetOrCreatePublicKey();

    /// <summary>Signs a challenge from the backend.</summary>
    string Sign(string nonce);

    /// <summary>
    /// Deletes the key, so the next start registers as a new device. Used after
    /// the backend reports the device was revoked.
    /// </summary>
    void Delete();
}

/// <summary>Identifying details about the machine, shown to the activating admin.</summary>
public interface IMachineFingerprintProvider
{
    MachineFingerprint Collect();
}

/// <param name="Hostname">Machine name.</param>
/// <param name="Os">Operating system name.</param>
/// <param name="OsVersion">Version string.</param>
/// <param name="MachineId">Stable machine identifier, where the platform has one.</param>
/// <param name="MacAddresses">Physical network adapters.</param>
/// <param name="Cpu">Processor description.</param>
/// <param name="RamGb">Installed memory, rounded to whole gigabytes.</param>
/// <param name="ScreenResolution">Primary screen, as <c>1920x1080</c>.</param>
public sealed record MachineFingerprint(
    string? Hostname,
    string? Os,
    string? OsVersion,
    string? MachineId,
    IReadOnlyCollection<string> MacAddresses,
    string? Cpu,
    int RamGb,
    string? ScreenResolution);

/// <summary>
/// Keeps the kiosk application the only thing on screen (plan section 7.1).
/// </summary>
/// <remarks>
/// Application-level lockdown only. The operating system level is the
/// provisioning script's job - Shell Launcher and Keyboard Filter on Windows,
/// cage and logind on Linux (plan section 7.12).
/// </remarks>
public interface IKioskLockdown
{
    /// <summary>Applies whatever this platform can enforce from inside the process.</summary>
    void Apply();

    /// <summary>Releases it, for the hidden admin exit.</summary>
    void Release();
}

/// <summary>Restarting the application and the machine (plan section 7.6).</summary>
public interface ISystemControl
{
    Task RestartApplicationAsync(CancellationToken cancellationToken = default);

    Task RebootAsync(CancellationToken cancellationToken = default);
}

/// <summary>A connected printer, as the kiosk sees it (plan section 7.7).</summary>
/// <param name="Name">Queue name, which is what printing uses.</param>
/// <param name="DriverName">Driver, shown to the administrator.</param>
/// <param name="IsOnline">Whether the queue reports itself ready.</param>
/// <param name="SupportsColor">Colour capability as detected.</param>
/// <param name="SupportsDuplex">Two-sided capability as detected.</param>
/// <param name="SupportsA3">A3 capability as detected.</param>
public sealed record DetectedPrinter(
    string Name,
    string? DriverName,
    bool IsOnline,
    bool SupportsColor,
    bool SupportsDuplex,
    bool SupportsA3);

/// <summary>
/// Printer discovery and printing (plan sections 7.6, 7.7).
/// </summary>
/// <remarks>
/// Windows goes through the spooler, Linux through CUPS, and a fake
/// implementation writes to a folder so the whole flow can be exercised on a
/// machine with no printer and in CI.
/// </remarks>
public interface IPrinterService
{
    Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>Watches for removable drives (plan section 2.1).</summary>
public interface IRemovableDriveWatcher
{
    /// <summary>Drives currently mounted and readable.</summary>
    Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default);
}

/// <param name="Path">Mount point or drive root.</param>
/// <param name="Label">Volume label, where there is one.</param>
/// <param name="TotalBytes">Capacity.</param>
/// <param name="AvailableBytes">Free space.</param>
public sealed record RemovableDrive(string Path, string? Label, long TotalBytes, long AvailableBytes);

/// <summary>Machine telemetry (plan section 7.9).</summary>
public interface ISystemMetrics
{
    Task<SystemSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <param name="CpuPercent">Processor use, 0-100.</param>
/// <param name="MemoryUsedPercent">Memory in use, 0-100.</param>
/// <param name="DiskFreeBytes">Free space on the volume holding the spool.</param>
/// <param name="UptimeSeconds">How long the machine has been running.</param>
public sealed record SystemSnapshot(
    double CpuPercent, double MemoryUsedPercent, long DiskFreeBytes, long UptimeSeconds);
