using System.Diagnostics;
using System.Runtime.Versioning;
using System.Net.NetworkInformation;
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
    public Task RestartApplicationAsync(CancellationToken cancellationToken = default)
        => RunAsync("systemctl", "restart neoprint-kiosk", cancellationToken);

    public Task RebootAsync(CancellationToken cancellationToken = default)
        => RunAsync("systemctl", "reboot", cancellationToken);

    private static async Task RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = $"{file} {arguments}",
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Could not start sudo {file} {arguments}.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Removable drives on Linux, read from <c>/proc/mounts</c>.
/// </summary>
/// <remarks>
/// udisks2 mounts a stick under <c>/media/kiosk</c> or <c>/run/media</c>, with
/// noexec, under a polkit rule the provisioning script installs
/// (plan sections 2.1, 7.12).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxRemovableDriveWatcher : IRemovableDriveWatcher
{
    private static readonly string[] MountRoots = ["/media/", "/run/media/"];

    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default)
    {
        var drives = new List<RemovableDrive>();

        if (!File.Exists("/proc/mounts"))
        {
            return Task.FromResult<IReadOnlyList<RemovableDrive>>(drives);
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

        return Task.FromResult<IReadOnlyList<RemovableDrive>>(drives);
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
