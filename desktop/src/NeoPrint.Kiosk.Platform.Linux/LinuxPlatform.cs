using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Linux;

/// <summary>
/// Where the kiosk keeps its data on Linux (plan section 7.6).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxAppPaths : IAppPaths
{
    private const string Root = "/var/lib/neoprint";

    public string DeviceDirectory => Path.Combine(Root, "device");

    public string SpoolDirectory => Path.Combine(Root, "spool");

    public string StateDirectory => Path.Combine(Root, "state");

    public string LogDirectory => "/var/log/neoprint";

    public void EnsureCreated()
    {
        foreach (var directory in new[] { DeviceDirectory, SpoolDirectory, StateDirectory, LogDirectory })
        {
            Directory.CreateDirectory(directory);

            // Owner only: the spool holds decrypted-on-demand documents and the
            // device directory holds the private key (plan section 2.13).
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>
/// The device key on Linux: a PEM file readable only by the service account
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// Linux has no equivalent of a non-exportable CNG key without bringing in a
/// TPM stack, so the protection here is filesystem permissions plus the
/// lockdown that stops anyone reaching a shell on the machine at all
/// (plan section 7.12).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceKeyStore : IDeviceKeyStore
{
    private readonly IAppPaths _paths;

    public LinuxDeviceKeyStore(IAppPaths paths) => _paths = paths;

    private string KeyPath => Path.Combine(_paths.DeviceDirectory, "key.pem");

    public bool Exists => File.Exists(KeyPath);

    public string GetOrCreatePublicKey()
    {
        using var key = LoadOrCreate();

        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public string Sign(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        using var key = LoadOrCreate();

        return Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(nonce),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Delete()
    {
        if (File.Exists(KeyPath))
        {
            File.Delete(KeyPath);
        }
    }

    private ECDsa LoadOrCreate()
    {
        _paths.EnsureCreated();

        if (File.Exists(KeyPath))
        {
            var existing = ECDsa.Create();
            try
            {
                existing.ImportFromPem(File.ReadAllText(KeyPath));
                return existing;
            }
            catch
            {
                existing.Dispose();
                throw;
            }
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        try
        {
            File.WriteAllText(KeyPath, created.ExportECPrivateKeyPem());
            File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }
}

/// <summary>Machine details from <c>/proc</c>, <c>/etc</c> and the BCL.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxMachineFingerprintProvider : IMachineFingerprintProvider
{
    public MachineFingerprint Collect() => new(
        Environment.MachineName,
        ReadOsName(),
        Environment.OSVersion.VersionString,
        ReadMachineId(),
        ReadMacAddresses(),
        ReadCpuModel(),
        ReadRamGb(),
        // The screen is read from the compositor at runtime by the UI layer;
        // it is not available from /proc.
        null);

    private static string? ReadOsName()
    {
        // /etc/os-release is the standard place for a human-readable name.
        const string path = "/etc/os-release";

        if (!File.Exists(path))
        {
            return "Linux";
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                return line["PRETTY_NAME=".Length..].Trim('"');
            }
        }

        return "Linux";
    }

    private static string? ReadMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string[] ReadMacAddresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Select(adapter => adapter.GetPhysicalAddress().ToString())
            .Where(address => address.Length == 12)
            .Select(FormatMac)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];

    private static string FormatMac(string raw)
        => string.Join(':', Enumerable.Range(0, 6).Select(index => raw.Substring(index * 2, 2)));

    private static string? ReadCpuModel()
    {
        const string path = "/proc/cpuinfo";

        if (!File.Exists(path))
        {
            return null;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("model name", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                return separator < 0 ? null : line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private static int ReadRamGb()
    {
        const string path = "/proc/meminfo";

        if (!File.Exists(path))
        {
            return 0;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                continue;
            }

            var digits = new string(line.Where(char.IsAsciiDigit).ToArray());

            // MemTotal is in kB; round to the nearest whole gigabyte.
            return long.TryParse(digits, out var kilobytes)
                ? (int)Math.Round(kilobytes / 1024d / 1024d)
                : 0;
        }

        return 0;
    }
}

/// <summary>
/// Restarting on Linux, through systemd.
/// </summary>
/// <remarks>
/// The service account is allowed exactly these commands through a narrow
/// sudoers rule the provisioning script installs (plan section 7.12).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxSystemControl : ISystemControl
{
    /// <summary>
    /// The kiosk service restarts the UI as soon as it exits (Restart=always),
    /// so exiting is the restart - no privilege needed (docs/TASKS.md T-078).
    /// </summary>
    public Task RestartApplicationAsync(CancellationToken cancellationToken = default)
    {
        Environment.Exit(0);
        return Task.CompletedTask;
    }

    /// <summary>Allowed to the kiosk account by the provisioning script's sudoers rule.</summary>
    public Task RebootAsync(CancellationToken cancellationToken = default)
        => RunAsync("systemctl", "reboot", cancellationToken);

    /// <summary>
    /// Stops the kiosk session and puts a login prompt on the screen
    /// (<c>neoprint-maintenance.service</c>, installed by the provisioning
    /// script); <c>systemctl start neoprint-kiosk</c> brings the kiosk back.
    /// </summary>
    public Task OpenMaintenanceAsync(CancellationToken cancellationToken = default)
        => RunAsync("systemctl", "start neoprint-maintenance.service", cancellationToken);

    private static async Task RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"-n {file} {arguments}",
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Could not start sudo {file} {arguments}.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads <c>lsblk -J</c>; plain parsing, testable on any platform.</summary>
public static class RemovableBlockDevices
{
    /// <summary>
    /// The removable filesystems <c>lsblk -J</c> lists without a mount point.
    /// Encrypted volumes are left alone: the kiosk never asks for a password.
    /// </summary>
    public static IReadOnlyList<string> UnmountedRemovable(string lsblkJson)
    {
        ArgumentNullException.ThrowIfNull(lsblkJson);

        var found = new List<string>();

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(lsblkJson);

            if (document.RootElement.TryGetProperty("blockdevices", out var devices))
            {
                foreach (var device in devices.EnumerateArray())
                {
                    Collect(device, removable: false, found);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Nothing readable, nothing to mount.
        }

        return found;
    }

    private static void Collect(System.Text.Json.JsonElement device, bool removable, List<string> found)
    {
        // A partition inherits "removable" from its disk.
        removable |= Flag(device, "rm") || Flag(device, "hotplug");

        var hasChildren = device.TryGetProperty("children", out var children) && children.GetArrayLength() > 0;
        var fileSystem = device.TryGetProperty("fstype", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.String ? fs.GetString() : null;

        if (removable && !hasChildren && !string.IsNullOrEmpty(fileSystem) && fileSystem != "crypto_LUKS" && fileSystem != "swap"
            && !IsMounted(device) && device.TryGetProperty("name", out var name) && name.GetString() is { } path)
        {
            found.Add(path);
        }

        if (hasChildren)
        {
            foreach (var child in children.EnumerateArray())
            {
                Collect(child, removable, found);
            }
        }
    }

    // lsblk writes "rm": true in newer versions and "rm": "1" in older ones.
    private static bool Flag(System.Text.Json.JsonElement device, string name)
        => device.TryGetProperty(name, out var value)
           && (value.ValueKind == System.Text.Json.JsonValueKind.True
               || (value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() == "1"));

    private static bool IsMounted(System.Text.Json.JsonElement device)
    {
        if (device.TryGetProperty("mountpoints", out var points) && points.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return points.EnumerateArray().Any(point => point.ValueKind == System.Text.Json.JsonValueKind.String);
        }

        return device.TryGetProperty("mountpoint", out var point) && point.ValueKind == System.Text.Json.JsonValueKind.String;
    }
}

/// <summary>
/// Removable drives on Linux: mounted by the kiosk itself, then read from
/// <c>/proc/mounts</c>.
/// </summary>
/// <remarks>
/// <para>
/// With no desktop there is no automounter, so a stick would never appear
/// (docs/TASKS.md T-078): the kiosk finds unmounted removable filesystems with
/// <c>lsblk</c> and asks udisks2 to mount them. The polkit rule the
/// provisioning script installs lets only the kiosk account do that, and
/// udisks2 mounts with noexec (plan sections 2.1, 7.12).
/// </para>
/// <para>
/// A stick that would not mount (an unknown filesystem) is tried again after
/// half a minute, not every two seconds.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxRemovableDriveWatcher : IRemovableDriveWatcher
{
    private static readonly string[] MountRoots = ["/media/", "/run/media/"];
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly ICommandRunner _commands;
    private readonly Dictionary<string, DateTime> _failed = new(StringComparer.Ordinal);

    public LinuxRemovableDriveWatcher(ICommandRunner? commands = null) => _commands = commands ?? new ProcessCommandRunner();

    public async Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default)
    {
        await MountNewAsync(cancellationToken).ConfigureAwait(false);

        return Mounted();
    }

    /// <summary>Mounts what was plugged in since the last look.</summary>
    public async Task MountNewAsync(CancellationToken cancellationToken)
    {
        (int ExitCode, string Output) listing;

        try
        {
            listing = await _commands
                .RunAsync("lsblk", ["-J", "-p", "-o", "NAME,RM,HOTPLUG,TYPE,FSTYPE,MOUNTPOINTS"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return;
        }

        if (listing.ExitCode != 0)
        {
            return;
        }

        foreach (var device in RemovableBlockDevices.UnmountedRemovable(listing.Output))
        {
            if (_failed.TryGetValue(device, out var at) && DateTime.UtcNow - at < RetryAfter)
            {
                continue;
            }

            var (exit, _) = await _commands
                .RunAsync("udisksctl", ["mount", "--no-user-interaction", "-b", device], cancellationToken)
                .ConfigureAwait(false);

            if (exit == 0)
            {
                _failed.Remove(device);
            }
            else
            {
                _failed[device] = DateTime.UtcNow;
            }
        }
    }

    private static IReadOnlyList<RemovableDrive> Mounted()
    {
        var drives = new List<RemovableDrive>();

        if (!File.Exists("/proc/mounts"))
        {
            return drives;
        }

        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            // /proc/mounts escapes spaces as \040.
            var mountPoint = fields[1].Replace("\\040", " ", StringComparison.Ordinal);

            if (!MountRoots.Any(root => mountPoint.StartsWith(root, StringComparison.Ordinal)))
            {
                continue;
            }

            try
            {
                var info = new DriveInfo(mountPoint);
                drives.Add(new RemovableDrive(
                    mountPoint,
                    Path.GetFileName(mountPoint.TrimEnd('/')),
                    info.TotalSize,
                    info.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stick pulled out between reading the line and querying it.
            }
        }

        return drives;
    }
}

/// <summary>
/// Application-level lockdown on Linux.
/// </summary>
/// <remarks>
/// Almost nothing to do here: <c>cage</c> gives the application the whole
/// display with no compositor shortcuts, no desktop and no notification daemon,
/// so there is nothing for the process itself to suppress (plan section 7.12).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxKioskLockdown : IKioskLockdown
{
    public void Apply()
    {
    }

    public void Release()
    {
    }
}
