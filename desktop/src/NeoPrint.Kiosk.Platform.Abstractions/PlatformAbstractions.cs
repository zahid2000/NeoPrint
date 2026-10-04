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

/// <summary>
/// The key combinations a customer could leave the kiosk with (plan section
/// 7.12, Keyboard Filter's list): the Windows keys, Alt+Tab, Alt+Esc,
/// Alt+F4, Alt+Space and Ctrl+Esc. Kept apart from the hook so it is tested
/// on every platform.
/// </summary>
public static class KeyboardLockdownRules
{
    public const int Tab = 0x09;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int LeftWindows = 0x5B;
    public const int RightWindows = 0x5C;
    public const int Applications = 0x5D;
    public const int F4 = 0x73;

    /// <param name="virtualKey">The Win32 virtual-key code.</param>
    public static bool IsBlocked(int virtualKey, bool alt, bool control) => virtualKey switch
    {
        LeftWindows or RightWindows or Applications => true,
        Tab or Escape or F4 or Space when alt => true,
        Escape when control => true,
        _ => false,
    };
}

/// <summary>Restarting the application and the machine (plan section 7.6).</summary>
public interface ISystemControl
{
    Task RestartApplicationAsync(CancellationToken cancellationToken = default);

    Task RebootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the machine to a technician from the admin menu (plan section
    /// 7.12): Windows signs the kiosk account out to the logon screen, Linux
    /// stops the kiosk session and opens a console login.
    /// </summary>
    Task OpenMaintenanceAsync(CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Where the printer stands, in the one vocabulary every platform maps to
    /// (plan section 7.4: Online, Offline, OutOfPaper, PaperJam, TonerLow, Error).
    /// </summary>
    Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints a ready PDF and waits until the printer has taken it, or until it
    /// fails. The page range, N-up and duplex layout are already in the PDF
    /// (plan section 7.4); only copies, sides and colour travel with it.
    /// </summary>
    Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The printer's condition (plan section 7.4, <c>PrinterStatus</c>).</summary>
public enum PrinterState
{
    Online = 0,
    Offline = 1,
    OutOfPaper = 2,
    PaperJam = 3,

    /// <summary>Still prints; worth a warning, not a stop.</summary>
    TonerLow = 4,
    Error = 5,

    /// <summary>No queue by that name on this machine.</summary>
    NotFound = 6,
}

/// <summary>One document for the printer.</summary>
/// <param name="Printer">Queue name.</param>
/// <param name="PdfPath">The prepared PDF, decrypted into a private working folder.</param>
/// <param name="Copies">Copies of the whole document.</param>
/// <param name="Duplex">Both sides of the sheet (long edge).</param>
/// <param name="Color">Colour; false prints in grey.</param>
/// <param name="Paper">A4 or A3.</param>
/// <param name="JobName">What the spooler shows: the job code, never the file name.</param>
/// <param name="ContentType">
/// What the file is: the prepared PDF, or the customer's original file when
/// the printer speaks its format natively (plan section 2.2).
/// </param>
public sealed record PrintRequest(
    string Printer,
    string PdfPath,
    int Copies,
    bool Duplex,
    bool Color,
    string Paper,
    string JobName,
    string ContentType = PrintContentTypes.Pdf)
{
    /// <summary>
    /// Told the spooler's reference for the job the moment it is submitted
    /// (the CUPS request id, the Windows document name), so a restart can find
    /// it again (<see cref="IPrintJobTracker"/>).
    /// </summary>
    public Action<string>? Submitted { get; init; }

    /// <summary>
    /// Print at 100%, never fitted to the paper: scans and layouts, where a
    /// card must come out card-sized (plan section 2.11, "Real ölçü 100%").
    /// </summary>
    public bool ActualSize { get; init; }
}

/// <summary>A rectangle on the sheet, in whatever unit the caller works in.</summary>
public readonly record struct Placement(double X, double Y, double Width, double Height);

/// <summary>
/// Where a page goes on the sheet (plan sections 7.4, 2.11): fitted to the
/// printable area, or at its real size in the middle of the paper.
/// </summary>
/// <remarks>
/// Kept apart from the Windows printer code so it can be tested anywhere:
/// the arithmetic decides whether an ID card prints 85.6 mm wide.
/// </remarks>
public static class PagePlacement
{
    /// <summary>The page shrunk or grown to fill the printable area, keeping its shape, centred.</summary>
    public static Placement Fit(Placement printable, double pageWidth, double pageHeight)
    {
        var scale = Math.Min(printable.Width / pageWidth, printable.Height / pageHeight);
        var width = pageWidth * scale;
        var height = pageHeight * scale;

        return new Placement(
            printable.X + ((printable.Width - width) / 2),
            printable.Y + ((printable.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// The page at its own size, centred on the whole sheet - not on the
    /// printable area, whose margins differ from side to side on most printers.
    /// </summary>
    /// <param name="sheet">The whole sheet, in the drawing's coordinates.</param>
    public static Placement Actual(Placement sheet, double pageWidth, double pageHeight)
        => new(
            sheet.X + ((sheet.Width - pageWidth) / 2),
            sheet.Y + ((sheet.Height - pageHeight) / 2),
            pageWidth,
            pageHeight);
}

/// <summary>
/// The page description languages a printer may take as they are (plan
/// section 2.2, IPP <c>document-format-supported</c>), by MIME type.
/// </summary>
public static class PrintContentTypes
{
    public const string Pdf = "application/pdf";
    public const string PostScript = "application/postscript";
    public const string Pcl = "application/vnd.hp-pcl";
    public const string Jpeg = "image/jpeg";
    public const string Xps = "application/vnd.ms-xpsdocument";
    public const string Oxps = "application/oxps";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = Pdf,
        ["ps"] = PostScript,
        ["eps"] = PostScript,
        ["pcl"] = Pcl,
        ["prn"] = Pcl,
        ["jpg"] = Jpeg,
        ["jpeg"] = Jpeg,
        ["xps"] = Xps,
        ["oxps"] = Oxps,
    };

    /// <summary>The MIME type of a file a printer might take natively; null for anything else (DOCX, PNG ...).</summary>
    public static string? ForExtension(string? extension)
        => extension is not null && ByExtension.TryGetValue(extension.TrimStart('.'), out var type) ? type : null;

    /// <summary>A file extension for a spooled file of this type.</summary>
    public static string ExtensionFor(string? contentType) => contentType switch
    {
        PostScript => "ps",
        Pcl => "pcl",
        Jpeg => "jpg",
        Xps => "xps",
        Oxps => "oxps",
        _ => "pdf",
    };
}

/// <summary>
/// Which formats a printer takes as they are (plan section 7.6,
/// <c>IPrinterFormatSupport</c>): the rule of plan section 2.2 sends the
/// customer's original file only to a printer that lists its format.
/// </summary>
public interface IPrinterFormatSupport
{
    /// <summary>MIME types (<see cref="PrintContentTypes"/>); empty when the printer is not known or cannot say.</summary>
    Task<IReadOnlySet<string>> GetNativeFormatsAsync(string printer, CancellationToken cancellationToken = default);
}

/// <summary>What the spooler knows about a job, asked after the kiosk restarted (plan section 7.12).</summary>
public enum SpoolerJobState
{
    /// <summary>The spooler has no such job: never submitted, or gone without a trace.</summary>
    NotFound = 0,

    /// <summary>Still queued or printing.</summary>
    Pending = 1,

    /// <summary>Printed to the end.</summary>
    Completed = 2,

    /// <summary>Stopped, cancelled or aborted.</summary>
    Failed = 3,
}

/// <summary>
/// Finds a job the kiosk sent before it restarted (plan section 7.12, step 4:
/// "sistem spooler-də həmin job axtarılır").
/// </summary>
public interface IPrintJobTracker
{
    /// <param name="reference">What the print reported through <see cref="PrintRequest.Submitted"/>.</param>
    Task<SpoolerJobState> GetJobStateAsync(string printer, string reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where a network printer is, for SNMP (plan section 7.10); null for a USB
/// or local printer.
/// </summary>
public interface IPrinterAddressSource
{
    Task<string?> GetNetworkAddressAsync(string printer, CancellationToken cancellationToken = default);
}

/// <summary>
/// The printer's own count of what it has printed (Printer-MIB,
/// <c>prtMarkerLifeCount</c>) - the second witness to "did it print?"
/// (plan section 2.5, <c>print_attempts</c>).
/// </summary>
public interface IPrinterPageCounter
{
    /// <returns>Null when the printer is not on the network or does not answer.</returns>
    Task<PageCount?> ReadAsync(string printer, CancellationToken cancellationToken = default);
}

/// <summary>A reading of the printer's counter.</summary>
/// <param name="Value">The lifetime count.</param>
/// <param name="CountsSheets">True when the printer counts sheets; false when it counts impressions (sides).</param>
public sealed record PageCount(long Value, bool CountsSheets);

/// <summary>A platform that sends every job as the prepared PDF.</summary>
public sealed class NoNativeFormats : IPrinterFormatSupport
{
    public static readonly NoNativeFormats Instance = new();

    public Task<IReadOnlySet<string>> GetNativeFormatsAsync(string printer, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}

/// <summary>How a print went.</summary>
/// <param name="ErrorCode">A message key when it failed: <c>printer.outOfPaper</c>.</param>
public sealed record PrintOutcome(bool Succeeded, string? ErrorCode = null)
{
    public static readonly PrintOutcome Done = new(true);

    public static PrintOutcome Failed(string errorCode) => new(false, errorCode);
}

/// <summary>
/// The key that encrypts the spool, kept so it never leaves the machine in
/// the clear (plan sections 2.13, 7.5; docs/SECURITY_THREAT_MODEL.md).
/// </summary>
/// <remarks>
/// Windows wraps it with a non-exportable CNG key of the machine; Linux keeps
/// it in a file only the kiosk user can read. Deleted with the device key
/// when the device is detached, which makes whatever is left in the spool
/// unreadable.
/// </remarks>
public interface ISpoolKeyStore
{
    /// <summary>The 256-bit key, created on first use.</summary>
    byte[] GetOrCreateKey();

    void Delete();
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

/// <summary>The message keys a printer state stops a job with (plan section 7.4).</summary>
public static class PrinterErrors
{
    public static string For(PrinterState state) => state switch
    {
        PrinterState.Offline => "printer.offline",
        PrinterState.OutOfPaper => "printer.outOfPaper",
        PrinterState.PaperJam => "printer.paperJam",
        PrinterState.NotFound => "printer.notFound",
        _ => "printer.error",
    };
}

/// <summary>
/// Runs a converter (LibreOffice, Ghostscript) on a user's file: in a process
/// of its own, with a time limit and a memory limit, killed with everything it
/// started when either runs out (CLAUDE.md: user files are untrusted input).
/// </summary>
/// <remarks>
/// Windows puts the process in a Job Object; Linux starts it under
/// <c>prlimit</c>, as the <c>neoprint-convert</c> account whose traffic the
/// firewall drops; Windows blocks the converters' outbound traffic by program
/// (provisioning scripts, docs/TASKS.md T-075).
/// </remarks>
public interface IConverterProcessRunner
{
    Task<ConverterRun> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>How a converter run ended.</summary>
public enum ConverterRunOutcome
{
    Exited = 0,
    TimedOut = 1,

    /// <summary>The program is not installed on this kiosk.</summary>
    NotStarted = 2,
}

public sealed record ConverterRun(ConverterRunOutcome Outcome, int ExitCode, string StandardError);
