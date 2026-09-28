using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
        var refusals = new List<Exception>();

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
            catch (CryptographicException exception)
            {
                // No TPM, or the provider refused; try the next one. What each
                // one said is kept: the usual reason is that the process is not
                // elevated, and the key is machine-wide, which nobody can guess
                // from "could not create the device key".
                refusals.Add(exception);
            }
        }

        throw new InvalidOperationException(
            "Could not create the device key. Neither the platform nor the software key storage provider " +
            "accepted it. The key is machine-wide, so the usual cause is a process that is not elevated " +
            "(plan section 7.11). " +
            string.Join(" ", refusals.Select(refusal => $"[{refusal.Message.TrimEnd('.')}]")),
            refusals.Count > 0 ? refusals[^1] : null);
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
public sealed partial class WindowsSystemControl : ISystemControl
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

    /// <summary>
    /// Signs the kiosk account out. The logon screen follows, where the
    /// technician signs in with an administrator account - whose shell is the
    /// ordinary desktop (Shell Launcher's default profile, plan section 7.12).
    /// Autologon brings the kiosk back at the next start.
    /// </summary>
    public Task OpenMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        const uint LogOff = 0x0;
        const uint Force = 0x4;

        if (!ExitWindowsEx(LogOff | Force, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Task.CompletedTask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
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
/// is the belt-and-braces layer inside the process (plan sections 7.1, 7.12):
/// a low-level keyboard hook that swallows the combinations of
/// <see cref="KeyboardLockdownRules"/> while the kiosk runs. Ctrl+Alt+Del is
/// beyond any hook; Keyboard Filter takes care of it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsKioskLockdown : IKioskLockdown
{
    private const int KeyboardLowLevel = 13;
    private const int AltDown = 0x20;
    private const int Control = 0x11;

    private IntPtr _hook;

    // Held so the delegate the hook calls is not collected while it is installed.
    private LowLevelKeyboardProc? _callback;

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    /// <summary>Installs the hook; called on the UI thread, whose message loop runs it.</summary>
    public void Apply()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _callback = OnKey;
        _hook = SetWindowsHookEx(KeyboardLowLevel, _callback, GetModuleHandle(null), 0);

        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Release()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _callback = null;
        }
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyInfo>(data);
            var alt = (key.Flags & AltDown) != 0;
            var control = (GetAsyncKeyState(Control) & 0x8000) != 0;

            if (KeyboardLockdownRules.IsBlocked(key.VirtualKey, alt, control))
            {
                return 1;
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInfo
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetModuleHandle(string? name);
}
