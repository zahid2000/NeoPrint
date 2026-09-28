using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Linux;

/// <summary>Runs a command and returns what it printed; a seam for the CUPS tools.</summary>
public interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>The real command runner.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // CUPS answers in the language of the environment; the parsing below
        // reads the English words.
        start.Environment["LANG"] = "C";
        start.Environment["LC_ALL"] = "C";

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, output);
    }
}

/// <summary>
/// Printers through CUPS (plan sections 7.4, 7.6, 7.7): <c>lpstat</c> and
/// <c>lpoptions</c> to find them and read their state, <c>lp</c> to print,
/// and <c>lpstat -W not-completed</c> to wait for the job to leave.
/// </summary>
/// <remarks>
/// The kiosk user is in <c>lpadmin</c> (plan section 7.6), so no elevation is
/// needed. The job is named after the job code, never the file name.
/// </remarks>
public sealed partial class LinuxPrinterService : IPrinterService
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

    private readonly ICommandRunner _commands;
    private readonly TimeSpan _pollInterval;

    public LinuxPrinterService(ICommandRunner? commands = null, TimeSpan? pollInterval = null)
    {
        _commands = commands ?? new ProcessCommandRunner();
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var (_, names) = await _commands.RunAsync("lpstat", ["-e"], cancellationToken).ConfigureAwait(false);
        var printers = new List<DetectedPrinter>();

        foreach (var name in names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (_, options) = await _commands.RunAsync("lpoptions", ["-p", name, "-l"], cancellationToken).ConfigureAwait(false);
            var state = await GetStateAsync(name, cancellationToken).ConfigureAwait(false);

            printers.Add(new DetectedPrinter(
                name,
                DriverName: null,
                IsOnline: state is PrinterState.Online or PrinterState.TonerLow,
                SupportsColor: OptionOffers(options, "ColorModel", "RGB", "CMYK", "Color"),
                SupportsDuplex: OptionOffers(options, "Duplex", "DuplexNoTumble", "DuplexTumble") || OptionOffers(options, "sides", "two-sided"),
                SupportsA3: OptionOffers(options, "PageSize", "A3") || OptionOffers(options, "media", "A3")));
        }

        return printers;
    }

    public async Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
    {
        var (exit, output) = await _commands.RunAsync("lpstat", ["-l", "-p", printer], cancellationToken).ConfigureAwait(false);

        return ParseState(exit, output);
    }

    public async Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var state = await GetStateAsync(request.Printer, cancellationToken).ConfigureAwait(false);

        if (state is not (PrinterState.Online or PrinterState.TonerLow))
        {
            return PrintOutcome.Failed(PrinterErrors.For(state));
        }

        var (exit, output) = await _commands
            .RunAsync("lp", Arguments(request), cancellationToken)
            .ConfigureAwait(false);

        var match = RequestId().Match(output);

        if (exit != 0 || !match.Success)
        {
            return PrintOutcome.Failed("printer.error");
        }

        return await WaitAsync(request.Printer, match.Groups["id"].Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>lp</c> command line (plan section 7.4).</summary>
    public static IReadOnlyList<string> Arguments(PrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return
        [
            "-d", request.Printer,
            "-n", request.Copies.ToString(CultureInfo.InvariantCulture),
            "-t", request.JobName,
            "-o", request.Duplex ? "sides=two-sided-long-edge" : "sides=one-sided",
            "-o", $"media={request.Paper}",
            "-o", request.Color ? "ColorModel=RGB" : "ColorModel=Gray",
            "-o", "fit-to-page",
            request.PdfPath,
        ];
    }

    /// <summary>Reads <c>lpstat -l -p</c>: disabled, and the alerts CUPS reports.</summary>
    public static PrinterState ParseState(int exitCode, string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (exitCode != 0 || output.Contains("Invalid destination", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.NotFound;
        }

        if (output.Contains("media-empty", StringComparison.OrdinalIgnoreCase) || output.Contains("media-needed", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.OutOfPaper;
        }

        if (output.Contains("media-jam", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.PaperJam;
        }

        if (output.Contains("offline-report", StringComparison.OrdinalIgnoreCase) || output.Contains(" disabled", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.Offline;
        }

        if (output.Contains("toner-low", StringComparison.OrdinalIgnoreCase) || output.Contains("marker-supply-low", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterState.TonerLow;
        }

        return output.Contains("-error", StringComparison.OrdinalIgnoreCase) ? PrinterState.Error : PrinterState.Online;
    }

    private async Task<PrintOutcome> WaitAsync(string printer, string requestId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + JobTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var (_, pending) = await _commands.RunAsync("lpstat", ["-W", "not-completed", "-o", printer], cancellationToken).ConfigureAwait(false);

            if (!pending.Contains(requestId, StringComparison.Ordinal))
            {
                // Left the queue: printed, unless the printer stopped it.
                var state = await GetStateAsync(printer, cancellationToken).ConfigureAwait(false);
                return state is PrinterState.Online or PrinterState.TonerLow
                    ? PrintOutcome.Done
                    : PrintOutcome.Failed(PrinterErrors.For(state));
            }

            var state2 = await GetStateAsync(printer, cancellationToken).ConfigureAwait(false);

            if (state2 is PrinterState.OutOfPaper or PrinterState.PaperJam or PrinterState.Offline)
            {
                // Stopped part-way: the job is taken back so it cannot start
                // again on its own when somebody clears the jam.
                await _commands.RunAsync("cancel", [requestId], cancellationToken).ConfigureAwait(false);
                return PrintOutcome.Failed(PrinterErrors.For(state2));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        await _commands.RunAsync("cancel", [requestId], cancellationToken).ConfigureAwait(false);
        return PrintOutcome.Failed("printer.error");
    }

    private static bool OptionOffers(string lpoptions, string option, params string[] values)
    {
        foreach (var line in lpoptions.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0 || !line[..colon].Split('/')[0].Trim().Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var choices = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(choice => choice.TrimStart('*'));

            return choices.Any(choice => values.Any(value => choice.Contains(value, StringComparison.OrdinalIgnoreCase)));
        }

        return false;
    }

    [GeneratedRegex(@"request id is (?<id>\S+)")]
    private static partial Regex RequestId();
}

/// <summary>
/// The spool key in <c>/var/lib/neoprint/device/spool.key</c>, readable by the
/// kiosk user only (mode 600), next to the device key (plan section 7.11).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSpoolKeyStore : ISpoolKeyStore
{
    private readonly IAppPaths _paths;

    public LinuxSpoolKeyStore(IAppPaths paths) => _paths = paths;

    private string KeyPath => Path.Combine(_paths.DeviceDirectory, "spool.key");

    public byte[] GetOrCreateKey()
    {
        if (File.Exists(KeyPath))
        {
            return Convert.FromBase64String(File.ReadAllText(KeyPath));
        }

        Directory.CreateDirectory(_paths.DeviceDirectory);

        var key = RandomNumberGenerator.GetBytes(32);

        // Created with its permissions: there is no moment at which another
        // user could read it.
        using (var stream = new FileStream(KeyPath, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew,
                   Access = FileAccess.Write,
                   UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
               }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(Convert.ToBase64String(key));
        }

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

/// <summary>
/// A converter under <c>prlimit</c>: 2 GB of address space and 120 seconds of
/// processor, killed at the time limit with everything it started - the
/// backend's limits (ADR-0005).
/// </summary>
public sealed class LinuxConverterRunner : IConverterProcessRunner
{
    public const long MemoryBytes = 2L * 1024 * 1024 * 1024;
    public const int CpuSeconds = 120;

    public Task<ConverterRun> RunAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo("prlimit") { WorkingDirectory = workingDirectory };

        foreach (var argument in Wrap(executable, arguments))
        {
            start.ArgumentList.Add(argument);
        }

        return RunCheckedAsync(start, executable, timeout, cancellationToken);
    }

    /// <summary>The prlimit command line.</summary>
    public static IReadOnlyList<string> Wrap(string executable, IReadOnlyList<string> arguments)
        => [$"--as={MemoryBytes}", $"--cpu={CpuSeconds}", "--", executable, .. arguments];

    private static async Task<ConverterRun> RunCheckedAsync(
        ProcessStartInfo start, string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // prlimit starts even when the converter is missing, and then exits
        // 126/127; that is "not installed", not "the file is broken".
        if (!IsOnPath(executable))
        {
            return new ConverterRun(ConverterRunOutcome.NotStarted, -1, string.Empty);
        }

        var run = await ConverterProcesses.RunAsync(start, timeout, onStarted: null, cancellationToken).ConfigureAwait(false);

        return run.Outcome == ConverterRunOutcome.Exited && run.ExitCode is 126 or 127
            ? run with { Outcome = ConverterRunOutcome.NotStarted }
            : run;
    }

    private static bool IsOnPath(string executable)
        => Path.IsPathRooted(executable)
            ? File.Exists(executable)
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(folder => File.Exists(Path.Combine(folder, executable)));
}
