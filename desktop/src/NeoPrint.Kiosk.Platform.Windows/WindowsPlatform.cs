using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Windows;

/// <summary>
/// Where the kiosk keeps its data on Windows (plan section 7.6).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppPaths : IAppPaths
{
    private static string Root =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoPrint");

    public string DeviceDirectory => Path.Combine(Root, "device");

    public string SpoolDirectory => Path.Combine(Root, "spool");

    public string StateDirectory => Path.Combine(Root, "state");

    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var directory in new[] { DeviceDirectory, SpoolDirectory, StateDirectory, LogDirectory })
        {
            Directory.CreateDirectory(directory);
            Restrict(directory);
        }
    }

    /// <summary>
    /// Leaves only SYSTEM, Administrators and the kiosk account with access.
    /// </summary>
    /// <remarks>
    /// %ProgramData% is world-readable by default, and the spool holds customer
    /// documents (plan sections 2.13, 7.12).
    /// </remarks>
    private static void Restrict(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (var identity in new[]
                     {
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            // The UI runs as the limited kiosk account, which the provisioning
            // script creates; it needs to read and write its own state.
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException)
        {
            // Running as the limited kiosk account: the provisioning script has
            // already set these, and a failure here must not stop the kiosk.
        }
    }
}

/// <summary>
/// The device key on Windows: a non-exportable CNG key, held in the TPM where
/// one is available (plan section 7.11).
/// </summary>
/// <remarks>
/// Because the key cannot be exported, copying the disk to another machine does
/// not copy the kiosk's identity - which is the point.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceKeyStore : IDeviceKeyStore
{
    /// <summary>Machine-wide key container name.</summary>
    private const string KeyName = "NeoPrint.Device";

    /// <summary>TPM-backed provider, used when the machine has a TPM.</summary>
    private const string PlatformProvider = "Microsoft Platform Crypto Provider";

    /// <summary>Software fallback, still non-exportable.</summary>
    private const string SoftwareProvider = "Microsoft Software Key Storage Provider";

    public bool Exists => TryOpen(out var key) && Dispose(key);

    public string GetOrCreatePublicKey()
    {
        using var cng = OpenOrCreate();
        using var ecdsa = new ECDsaCng(cng);

        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    public string Sign(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        using var cng = OpenOrCreate();
        using var ecdsa = new ECDsaCng(cng);

        return Convert.ToBase64String(ecdsa.SignData(
            Encoding.UTF8.GetBytes(nonce),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Delete()
    {
        if (TryOpen(out var key))
        {
            key.Delete();
            key.Dispose();
        }
    }

    private static CngKey OpenOrCreate()
    {
        if (TryOpen(out var existing))
        {
            return existing;
        }

        // Prefer the TPM. A machine without one still gets a key that cannot be
        // exported, which is the property that matters.
        foreach (var provider in new[] { PlatformProvider, SoftwareProvider })
        {
            try
            {
                return CngKey.Create(
                    CngAlgorithm.ECDsaP256,
                    KeyName,
                    new CngKeyCreationParameters
                    {
                        Provider = new CngProvider(provider),
                        KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                        ExportPolicy = CngExportPolicies.None,
                        KeyUsage = CngKeyUsages.Signing,
                    });
            }
            catch (CryptographicException)
            {
                // No TPM, or the provider refused; try the next one.
            }
        }

        throw new InvalidOperationException(
            "Could not create the device key. Neither the platform nor the software key storage provider accepted it.");
    }

    private static bool TryOpen(out CngKey key)
    {
        foreach (var provider in new[] { PlatformProvider, SoftwareProvider })
        {
            try
            {
                if (CngKey.Exists(KeyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey))
                {
                    key = CngKey.Open(KeyName, new CngProvider(provider), CngKeyOpenOptions.MachineKey);
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // Provider unavailable on this machine.
            }
        }

        key = null!;
        return false;
    }

    private static bool Dispose(CngKey key)
    {
        key.Dispose();
        return true;
    }
}

/// <summary>Machine details from WMI and the BCL.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineFingerprintProvider : IMachineFingerprintProvider
{
    public MachineFingerprint Collect() => new(
        Environment.MachineName,
        "Windows",
        Environment.OSVersion.VersionString,
        ReadMachineGuid(),
        ReadMacAddresses(),
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        (int)Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d),
        null);

    /// <summary>
    /// The installation GUID Windows writes at setup, which is stable for the
    /// life of the installation.
    /// </summary>
    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");

            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string[] ReadMacAddresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Select(adapter => adapter.GetPhysicalAddress().ToString())
            .Where(address => address.Length == 12)
            .Select(raw => string.Join(':', Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase),
    ];
}

/// <summary>Restarting on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemControl : ISystemControl
{
    public Task RestartApplicationAsync(CancellationToken cancellationToken = default)
    {
        // Shell Launcher restarts the shell as soon as it exits, so exiting is
        // the restart (plan section 7.12).
        Environment.Exit(0);
        return Task.CompletedTask;
    }

    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/r /t 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start shutdown.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Removable drives on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemovableDriveWatcher : IRemovableDriveWatcher
{
    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default)
    {
        var drives = new List<RemovableDrive>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Removable)
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                drives.Add(new RemovableDrive(
                    drive.RootDirectory.FullName,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed between enumerating and querying.
            }
        }

        return Task.FromResult<IReadOnlyList<RemovableDrive>>(drives);
    }
}

/// <summary>
/// Application-level lockdown on Windows.
/// </summary>
/// <remarks>
/// The operating system level - Shell Launcher and Keyboard Filter - is the
/// provisioning script's job, and those are the parts that actually hold. This
/// is the belt-and-braces layer inside the process (plan sections 7.1, 7.12).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsKioskLockdown : IKioskLockdown
{
    public void Apply()
    {
        // The low-level keyboard hook lands with the full lockdown in step 3;
        // until then Shell Launcher and Keyboard Filter carry it.
    }

    public void Release()
    {
    }
}
