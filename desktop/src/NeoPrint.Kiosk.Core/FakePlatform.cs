using System.Security.Cryptography;
using System.Text;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// Paths under a temporary directory, for tests and for running the kiosk on a
/// developer machine.
/// </summary>
public sealed class FakeAppPaths : IAppPaths
{
    public FakeAppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "neoprint-kiosk", Guid.NewGuid().ToString("N"));
    }

    public string Root { get; }

    public string DeviceDirectory => Path.Combine(Root, "device");

    public string SpoolDirectory => Path.Combine(Root, "spool");

    public string StateDirectory => Path.Combine(Root, "state");

    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        foreach (var directory in new[] { DeviceDirectory, SpoolDirectory, StateDirectory, LogDirectory })
        {
            Directory.CreateDirectory(directory);
        }
    }
}

/// <summary>
/// A device key held in a plain file, for tests and developer machines.
/// </summary>
/// <remarks>
/// Never used on a real kiosk: there the key is a non-exportable CNG key on
/// Windows and a mode 600 file owned by the service account on Linux
/// (plan section 7.11).
/// </remarks>
public sealed class FakeDeviceKeyStore : IDeviceKeyStore
{
    private readonly IAppPaths _paths;

    public FakeDeviceKeyStore(IAppPaths paths) => _paths = paths;

    private string KeyPath => Path.Combine(_paths.DeviceDirectory, "key.pem");

    public bool Exists => File.Exists(KeyPath);

    public string GetOrCreatePublicKey()
    {
        using var key = LoadOrCreate();
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public string Sign(string nonce)
    {
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
            return created;
        }
        catch
        {
            created.Dispose();
            throw;
        }
    }
}

/// <summary>A fixed fingerprint, for tests.</summary>
public sealed class FakeMachineFingerprintProvider : IMachineFingerprintProvider
{
    private readonly MachineFingerprint _fingerprint;

    public FakeMachineFingerprintProvider(MachineFingerprint? fingerprint = null)
    {
        _fingerprint = fingerprint ?? new MachineFingerprint(
            "fake-kiosk",
            "Fake OS",
            "1.0",
            Guid.NewGuid().ToString("N"),
            ["AA:BB:CC:DD:EE:FF"],
            "Fake CPU",
            8,
            "1920x1080");
    }

    public MachineFingerprint Collect() => _fingerprint;
}

/// <summary>
/// A printer that writes what it is asked to print into a folder
/// (plan section 7.6).
/// </summary>
/// <remarks>
/// This is what makes the whole print flow testable on a machine with no
/// printer and in CI. Enabled by <c>Kiosk:UseFakePrinter</c>, which is always
/// false in a release build.
/// </remarks>
public sealed class FakePrinterService : IPrinterService
{
    private readonly List<DetectedPrinter> _printers;

    public FakePrinterService(IEnumerable<DetectedPrinter>? printers = null)
    {
        _printers = printers?.ToList() ??
        [
            new DetectedPrinter(
                "NeoPrint Fake Printer",
                "neoprint-fake",
                IsOnline: true,
                SupportsColor: true,
                SupportsDuplex: true,
                SupportsA3: true),
        ];
    }

    /// <summary>Makes the queue report itself offline, to exercise that path.</summary>
    public bool SimulateOffline { get; set; }

    public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DetectedPrinter>>(
            SimulateOffline
                ? [.. _printers.Select(printer => printer with { IsOnline = false })]
                : _printers);
}

/// <summary>No removable drives, for tests.</summary>
public sealed class FakeRemovableDriveWatcher : IRemovableDriveWatcher
{
    public List<RemovableDrive> Drives { get; } = [];

    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemovableDrive>>(Drives);
}

/// <summary>Records what it was asked to do, for tests.</summary>
public sealed class FakeSystemControl : ISystemControl
{
    public int ApplicationRestarts { get; private set; }

    public int Reboots { get; private set; }

    public Task RestartApplicationAsync(CancellationToken cancellationToken = default)
    {
        ApplicationRestarts++;
        return Task.CompletedTask;
    }

    public Task RebootAsync(CancellationToken cancellationToken = default)
    {
        Reboots++;
        return Task.CompletedTask;
    }
}

/// <summary>Does nothing, for tests.</summary>
public sealed class FakeKioskLockdown : IKioskLockdown
{
    public bool IsApplied { get; private set; }

    public void Apply() => IsApplied = true;

    public void Release() => IsApplied = false;
}
