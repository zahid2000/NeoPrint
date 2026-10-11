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
public sealed class FakePrinterService : IPrinterService, IPrinterFormatSupport, IPrintJobTracker, IPrinterPageCounter
{
    /// <summary>The name the fake printer is listed under (plan section 7.7).</summary>
    public const string FakePrinterName = "NeoPrint Fake Printer";

    private readonly List<DetectedPrinter> _printers;
    private readonly string? _outputDirectory;

    /// <param name="outputDirectory">
    /// Where printed PDFs land (<c>spool/printed</c>); a file named
    /// <c>printer-state.txt</c> there holding a state name (<c>OutOfPaper</c>,
    /// <c>PaperJam</c>, ...) makes the printer report it, to walk through the
    /// error paths by hand.
    /// </param>
    public FakePrinterService(IEnumerable<DetectedPrinter>? printers = null, string? outputDirectory = null)
    {
        _printers = printers?.ToList() ??
        [
            new DetectedPrinter(
                FakePrinterName,
                "neoprint-fake",
                IsOnline: true,
                SupportsColor: true,
                SupportsDuplex: true,
                SupportsA3: true),
        ];
        _outputDirectory = outputDirectory;
    }

    /// <summary>Makes the queue report itself offline, to exercise that path.</summary>
    public bool SimulateOffline { get; set; }

    /// <summary>A state to report, for tests; overrides the state file.</summary>
    public PrinterState? SimulatedState { get; set; }

    /// <summary>What the fake printer takes as it is: PDF, like most office printers.</summary>
    public HashSet<string> NativeFormats { get; } = [PrintContentTypes.Pdf];

    public Task<IReadOnlySet<string>> GetNativeFormatsAsync(string printer, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>>(
            _printers.Any(known => known.Name == printer) ? new HashSet<string>(NativeFormats) : new HashSet<string>());

    /// <summary>What the spooler says about a job, by reference; a printed job is Completed unless set here.</summary>
    public Dictionary<string, SpoolerJobState> SpoolerStates { get; } = new(StringComparer.Ordinal);

    /// <summary>The printer's page counter; null is a printer without one (USB, or no SNMP).</summary>
    public PageCount? Counter { get; set; }

    public Task<SpoolerJobState> GetJobStateAsync(string printer, string reference, CancellationToken cancellationToken = default)
        => Task.FromResult(
            SpoolerStates.TryGetValue(reference, out var state) ? state
            : Printed.Any(request => request.JobName == reference) ? SpoolerJobState.Completed
            : SpoolerJobState.NotFound);

    public Task<PageCount?> ReadAsync(string printer, CancellationToken cancellationToken = default) => Task.FromResult(Counter);

    /// <summary>What was printed, in order.</summary>
    public List<PrintRequest> Printed { get; } = [];

    public string? OutputDirectory => _outputDirectory;

    public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DetectedPrinter>>(
            SimulateOffline
                ? [.. _printers.Select(printer => printer with { IsOnline = false })]
                : _printers);

    public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
    {
        if (_printers.All(known => known.Name != printer))
        {
            return Task.FromResult(PrinterState.NotFound);
        }

        if (SimulateOffline)
        {
            return Task.FromResult(PrinterState.Offline);
        }

        if (SimulatedState is { } simulated)
        {
            return Task.FromResult(simulated);
        }

        var stateFile = _outputDirectory is null ? null : Path.Combine(_outputDirectory, "printer-state.txt");

        if (stateFile is not null
            && File.Exists(stateFile)
            && Enum.TryParse<PrinterState>(File.ReadAllText(stateFile).Trim(), ignoreCase: true, out var fromFile))
        {
            return Task.FromResult(fromFile);
        }

        return Task.FromResult(PrinterState.Online);
    }

    public async Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var state = await GetStateAsync(request.Printer, cancellationToken).ConfigureAwait(false);

        if (state is not (PrinterState.Online or PrinterState.TonerLow))
        {
            return PrintOutcome.Failed(PrinterErrors.For(state));
        }

        // printer-fail-next.txt: the next job breaks at the printer (it was
        // ready when the job started), once - the "printer error while printing"
        // path that printer-state.txt cannot reach, since a printer already in
        // error leaves the job waiting instead.
        var failNext = _outputDirectory is null ? null : Path.Combine(_outputDirectory, "printer-fail-next.txt");

        if (failNext is not null && File.Exists(failNext))
        {
            var failure = Enum.TryParse<PrinterState>(File.ReadAllText(failNext).Trim(), ignoreCase: true, out var named)
                ? named
                : PrinterState.Error;
            File.Delete(failNext);
            request.Submitted?.Invoke(request.JobName);
            return PrintOutcome.Failed(PrinterErrors.For(failure));
        }

        if (_outputDirectory is not null)
        {
            Directory.CreateDirectory(_outputDirectory);
            var target = Path.Combine(
                _outputDirectory,
                $"{request.JobName}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-x{request.Copies}{(request.Duplex ? "-duplex" : "")}{(request.Color ? "-color" : "-gray")}-{request.Paper}.{PrintContentTypes.ExtensionFor(request.ContentType)}");

            File.Copy(request.PdfPath, target, overwrite: false);
        }

        request.Submitted?.Invoke(request.JobName);
        Printed.Add(request);

        return PrintOutcome.Done;
    }
}

/// <summary>A spool key in a plain file, for tests and developer machines.</summary>
public sealed class FakeSpoolKeyStore : ISpoolKeyStore
{
    private readonly IAppPaths _paths;

    public FakeSpoolKeyStore(IAppPaths paths) => _paths = paths;

    private string KeyPath => Path.Combine(_paths.DeviceDirectory, "spool.key");

    public byte[] GetOrCreateKey()
    {
        _paths.EnsureCreated();

        if (File.Exists(KeyPath))
        {
            return Convert.FromBase64String(File.ReadAllText(KeyPath));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(KeyPath, Convert.ToBase64String(key));

        return key;
    }

    public void Delete()
    {
        if (File.Exists(KeyPath))
        {
            File.Delete(KeyPath);
        }
    }
}

/// <summary>No removable drives, for tests.</summary>
public sealed class FakeRemovableDriveWatcher : IRemovableDriveWatcher
{
    public List<RemovableDrive> Drives { get; } = [];

    public Task<IReadOnlyList<RemovableDrive>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RemovableDrive>>(Drives);
}

/// <summary>A machine with fixed readings, for tests and for running without a platform.</summary>
public sealed class FakeSystemMetrics : ISystemMetrics
{
    public SystemSnapshot Snapshot { get; set; } = new(
        Os: "Fake OS",
        OsVersion: "1.0",
        Hostname: "fake-kiosk",
        CpuPercent: 7.5,
        MemoryUsedBytes: 2L * 1024 * 1024 * 1024,
        MemoryTotalBytes: 8L * 1024 * 1024 * 1024,
        SystemDiskFreeBytes: 50L * 1024 * 1024 * 1024,
        SpoolDiskFreeBytes: 50L * 1024 * 1024 * 1024,
        UptimeSeconds: 3600,
        ConnectionType: "Ethernet",
        LocalIp: "192.168.0.10");

    public Task<SystemSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
}

/// <summary>Records what it was asked to do, for tests.</summary>
public sealed class FakeSystemControl : ISystemControl
{
    public int ApplicationRestarts { get; private set; }

    public int Reboots { get; private set; }

    public int MaintenanceOpened { get; private set; }

    public Task OpenMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        MaintenanceOpened++;
        return Task.CompletedTask;
    }

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
