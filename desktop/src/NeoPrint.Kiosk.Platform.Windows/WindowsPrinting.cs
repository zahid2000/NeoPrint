using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Pdfium;

namespace NeoPrint.Kiosk.Platform.Windows;

/// <summary>
/// Printers through the Windows spooler (plan sections 7.4, 7.6, 7.7).
/// </summary>
/// <remarks>
/// <para>
/// Found with WMI (<c>Win32_Printer</c>) and <see cref="PrinterSettings"/> for
/// colour, duplex and A3. Printed as the plan says: each PDF page drawn by
/// PDFium and handed to <see cref="PrintDocument"/>. The spooler job is then
/// watched through <c>Win32_PrintJob</c> until it leaves the queue.
/// </para>
/// <para>
/// "Microsoft Print to PDF" would ask where to save; it is given a file in the
/// spool's <c>printed</c> folder instead, which is what makes it usable as the
/// test printer of the step 3 DoD.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
/// <remarks>
/// Every job goes through the driver as the prepared PDF: the spooler's RAW
/// path would bypass the driver, and with it the copies, sides and grey the
/// customer paid for (docs/DECISIONS.md, native formats on Windows).
/// </remarks>
public sealed class WindowsPrinterService : IPrinterService, IPrinterFormatSupport, IPrintJobTracker, IPrinterAddressSource
{
    private const string PdfPrinter = "Microsoft Print to PDF";
    private const int PrintDpi = 300;
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

    private readonly string _printToFileFolder;

    public WindowsPrinterService(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _printToFileFolder = Path.Combine(paths.SpoolDirectory, "printed");
    }

    public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var printers = new List<DetectedPrinter>();

        using var searcher = new ManagementObjectSearcher("SELECT Name, DriverName FROM Win32_Printer");

        foreach (var printer in searcher.Get().Cast<ManagementObject>())
        {
            using (printer)
            {
                var name = printer["Name"] as string;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var settings = new PrinterSettings { PrinterName = name };

                printers.Add(new DetectedPrinter(
                    name,
                    printer["DriverName"] as string,
                    IsOnline: State(name) is PrinterState.Online or PrinterState.TonerLow,
                    SupportsColor: settings.IsValid && settings.SupportsColor,
                    SupportsDuplex: settings.IsValid && settings.CanDuplex,
                    SupportsA3: settings.IsValid && settings.PaperSizes.Cast<PaperSize>().Any(size => size.Kind == PaperKind.A3)));
            }
        }

        return Task.FromResult<IReadOnlyList<DetectedPrinter>>(printers);
    }

    public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
        => Task.FromResult(State(printer));

    public Task<IReadOnlySet<string>> GetNativeFormatsAsync(string printer, CancellationToken cancellationToken = default)
        => NoNativeFormats.Instance.GetNativeFormatsAsync(printer, cancellationToken);

    public async Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentType != PrintContentTypes.Pdf)
        {
            // Never asked for (no native formats are offered), refused if it is.
            return PrintOutcome.Failed("printer.error");
        }

        var state = State(request.Printer);

        if (state is not (PrinterState.Online or PrinterState.TonerLow))
        {
            return PrintOutcome.Failed(PrinterErrors.For(state));
        }

        var pdf = await File.ReadAllBytesAsync(request.PdfPath, cancellationToken).ConfigureAwait(false);

        try
        {
            // The spooler call blocks until the pages are spooled; off the
            // caller's thread so the screen keeps moving.
            // The document name is the spooler's handle on the job; kept
            // before a page is spooled, so a restart part-way can find it.
            request.Submitted?.Invoke(request.JobName);
            await Task.Run(() => Spool(request, pdf), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidPrinterException or ExternalException or PdfiumException)
        {
            return PrintOutcome.Failed(exception is PdfiumException ? "printer.spoolFailed" : "printer.error");
        }

        return await WaitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void Spool(PrintRequest request, byte[] pdf)
    {
        using var document = NeoPrint.Pdfium.PdfDocument.Load(pdf);
        using var print = new PrintDocument();

        print.DocumentName = request.JobName;
        print.PrintController = new StandardPrintController(); // no "Printing page n" dialog
        print.PrinterSettings.PrinterName = request.Printer;
        print.PrinterSettings.Copies = (short)Math.Clamp(request.Copies, 1, short.MaxValue);
        print.PrinterSettings.Collate = true;
        print.PrinterSettings.Duplex = request.Duplex ? Duplex.Vertical : Duplex.Simplex;
        print.DefaultPageSettings.Color = request.Color;

        var kind = request.Paper == "A3" ? PaperKind.A3 : PaperKind.A4;
        var paper = print.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(size => size.Kind == kind);

        if (paper is not null)
        {
            print.DefaultPageSettings.PaperSize = paper;
        }

        if (string.Equals(request.Printer, PdfPrinter, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(_printToFileFolder);
            print.PrinterSettings.PrintToFile = true;
            print.PrinterSettings.PrintFileName = Path.Combine(_printToFileFolder, $"{request.JobName}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.pdf");
        }

        var next = 0;

        print.QueryPageSettings += (_, e) =>
        {
            var (width, height) = document.PageSize(next);
            e.PageSettings.Landscape = width > height;
        };

        print.PrintPage += (_, e) =>
        {
            var (width, height) = document.PageSize(next);
            var pixels = (int)Math.Round(width / 72f * PrintDpi);
            var (bgra, w, h, stride) = document.RenderPage(next, pixels);

            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);

            try
            {
                using var bitmap = new Bitmap(w, h, stride, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());

                // The page range and N-up are already in the PDF (plan section 7.4).
                // Units are 1/100 inch. A scan or layout prints at its real
                // size in the middle of the sheet; the drawing's origin is the
                // printable corner, so the sheet starts at minus the hard
                // margins (plan section 2.11). Anything else fits the printable area.
                Placement placement;

                if (request.ActualSize)
                {
                    var sheet = new Placement(
                        -e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, e.PageBounds.Width, e.PageBounds.Height);
                    placement = PagePlacement.Actual(sheet, width / 72.0 * 100, height / 72.0 * 100);
                }
                else
                {
                    var area = e.MarginBounds;
                    placement = PagePlacement.Fit(new Placement(area.Left, area.Top, area.Width, area.Height), w, h);
                }

                e.Graphics!.DrawImage(
                    bitmap,
                    new RectangleF((float)placement.X, (float)placement.Y, (float)placement.Width, (float)placement.Height));
            }
            finally
            {
                handle.Free();
            }

            next++;
            e.HasMorePages = next < document.PageCount;
        };

        print.Print();
    }

    /// <summary>Waits for the spooler job to leave the queue, or to stop with an error.</summary>
    private async Task<PrintOutcome> WaitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + JobTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var job = FindJob(request.JobName);

            if (job is null || job.Value.Printed)
            {
                // Gone from the queue, or kept there as printed (a queue with
                // "keep printed documents" on): either way, finished.
                if (job is not null)
                {
                    CancelJob(request.JobName);
                }

                var after = State(request.Printer);
                return after is PrinterState.Online or PrinterState.TonerLow ? PrintOutcome.Done : PrintOutcome.Failed(PrinterErrors.For(after));
            }

            if (job.Value.Error)
            {
                var stopped = State(request.Printer);
                CancelJob(request.JobName);
                return PrintOutcome.Failed(stopped is PrinterState.Online ? "printer.error" : PrinterErrors.For(stopped));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        CancelJob(request.JobName);
        return PrintOutcome.Failed("printer.error");
    }

    /// <summary>Where the printer stands, from <c>Win32_Printer</c> (plan section 7.4).</summary>
    private static PrinterState State(string printer)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT WorkOffline, PrinterStatus, DetectedErrorState FROM Win32_Printer WHERE Name = '{Escape(printer)}'");

        var found = searcher.Get().Cast<ManagementObject>().FirstOrDefault();

        if (found is null)
        {
            return PrinterState.NotFound;
        }

        using (found)
        {
            var offline = found["WorkOffline"] as bool? == true || Convert.ToUInt16(found["PrinterStatus"] ?? (ushort)0) == 7;
            var error = Convert.ToUInt16(found["DetectedErrorState"] ?? (ushort)0);

            // Win32_Printer.DetectedErrorState.
            return error switch
            {
                4 => PrinterState.OutOfPaper,
                8 => PrinterState.PaperJam,
                9 => PrinterState.Offline,
                5 => PrinterState.TonerLow,
                6 or 7 or 10 or 11 => PrinterState.Error,
                _ when offline => PrinterState.Offline,
                _ => PrinterState.Online,
            };
        }
    }

    /// <summary>
    /// A job the kiosk sent before it restarted, found by its document name
    /// (plan section 7.12). The spooler forgets a printed job unless the queue
    /// keeps printed documents, so "not found" is a question the page counter
    /// answers.
    /// </summary>
    public Task<SpoolerJobState> GetJobStateAsync(string printer, string reference, CancellationToken cancellationToken = default)
    {
        var job = FindJob(reference);

        return Task.FromResult(job switch
        {
            null => SpoolerJobState.NotFound,
            { Printed: true } => SpoolerJobState.Completed,
            { Error: true } => SpoolerJobState.Failed,
            _ => SpoolerJobState.Pending,
        });
    }

    /// <summary>The host of a network printer's Standard TCP/IP port; null for USB and virtual printers.</summary>
    public Task<string?> GetNetworkAddressAsync(string printer, CancellationToken cancellationToken = default)
    {
        using var printers = new ManagementObjectSearcher($"SELECT PortName FROM Win32_Printer WHERE Name = '{Escape(printer)}'");
        var port = printers.Get().Cast<ManagementObject>().Select(found =>
        {
            using (found)
            {
                return found["PortName"] as string;
            }
        }).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(port))
        {
            return Task.FromResult<string?>(null);
        }

        using var ports = new ManagementObjectSearcher($"SELECT HostAddress FROM Win32_TCPIPPrinterPort WHERE Name = '{Escape(port)}'");
        var host = ports.Get().Cast<ManagementObject>().Select(found =>
        {
            using (found)
            {
                return found["HostAddress"] as string;
            }
        }).FirstOrDefault();

        return Task.FromResult(string.IsNullOrWhiteSpace(host) ? null : host);
    }

    private static (bool Error, bool Printed, string Name)? FindJob(string document)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT Name, Status, StatusMask FROM Win32_PrintJob WHERE Document = '{Escape(document)}'");

        foreach (var job in searcher.Get().Cast<ManagementObject>())
        {
            using (job)
            {
                var mask = Convert.ToUInt32(job["StatusMask"] ?? 0u);

                // JOB_STATUS_ERROR (0x2), _OFFLINE (0x20), _PAPEROUT (0x40), _BLOCKED_DEVQ (0x200), _USER_INTERVENTION (0x400);
                // JOB_STATUS_PRINTED (0x80), _COMPLETE (0x1000).
                return (
                    (mask & (0x2 | 0x20 | 0x40 | 0x200 | 0x400)) != 0,
                    (mask & (0x80 | 0x1000)) != 0,
                    job["Name"] as string ?? string.Empty);
            }
        }

        return null;
    }

    private static void CancelJob(string document)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PrintJob WHERE Document = '{Escape(document)}'");

        foreach (var job in searcher.Get().Cast<ManagementObject>())
        {
            using (job)
            {
                job.Delete();
            }
        }
    }

    /// <summary>WQL string quoting: printer names come from the admin panel.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}

/// <summary>
/// The spool key, kept on disk wrapped by a non-exportable RSA key of the
/// machine (CNG), next to the device key (plan section 7.11).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSpoolKeyStore : ISpoolKeyStore
{
    private const string KeyName = "NeoPrint.Spool";
    private static readonly CngProvider Provider = new("Microsoft Software Key Storage Provider");

    private readonly IAppPaths _paths;

    public WindowsSpoolKeyStore(IAppPaths paths) => _paths = paths;

    private string WrappedPath => Path.Combine(_paths.DeviceDirectory, "spool.key.wrapped");

    public byte[] GetOrCreateKey()
    {
        using var rsa = new RSACng(OpenOrCreate());

        if (File.Exists(WrappedPath))
        {
            return rsa.Decrypt(File.ReadAllBytes(WrappedPath), RSAEncryptionPadding.OaepSHA256);
        }

        Directory.CreateDirectory(_paths.DeviceDirectory);

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(WrappedPath, rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256));

        return key;
    }

    public void Delete()
    {
        if (File.Exists(WrappedPath))
        {
            File.Delete(WrappedPath);
        }

        if (CngKey.Exists(KeyName, Provider, CngKeyOpenOptions.MachineKey))
        {
            using var key = CngKey.Open(KeyName, Provider, CngKeyOpenOptions.MachineKey);
            key.Delete();
        }
    }

    private static CngKey OpenOrCreate()
        => CngKey.Exists(KeyName, Provider, CngKeyOpenOptions.MachineKey)
            ? CngKey.Open(KeyName, Provider, CngKeyOpenOptions.MachineKey)
            : CngKey.Create(
                CngAlgorithm.Rsa,
                KeyName,
                new CngKeyCreationParameters
                {
                    Provider = Provider,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Decryption,
                    Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) },
                });
}

/// <summary>
/// A converter inside a Job Object: 2 GB of memory for the whole tree,
/// everything killed when the job handle closes (ADR-0005's limits).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsConverterRunner : IConverterProcessRunner
{
    public const long MemoryBytes = 2L * 1024 * 1024 * 1024;

    public async Task<ConverterRun> RunAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var job = JobObject.Create(MemoryBytes);

        return await ConverterProcesses.RunAsync(start, timeout, process => job.Assign(process), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Win32 Job Object, through P/Invoke (kernel32).</summary>
    private sealed partial class JobObject : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;
        private const uint JobMemory = 0x200;

        private readonly IntPtr _handle;

        private JobObject(IntPtr handle) => _handle = handle;

        public static JobObject Create(long memoryBytes)
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);

            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var info = new ExtendedLimitInformation
            {
                Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose | JobMemory },
                JobMemoryLimit = (UIntPtr)(ulong)memoryBytes,
            };

            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(info, pointer, fDeleteOld: false);

                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, pointer, (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            return new JobObject(handle);
        }

        public void Assign(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                // Without the limit the conversion does not run at all.
                process.Kill(entireProcessTree: true);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public void Dispose() => CloseHandle(_handle);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
