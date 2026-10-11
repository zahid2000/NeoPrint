using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Extensions.Logging;

using NeoPrint.Converter;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>How a sandboxed process ended.</summary>
public enum SandboxOutcome
{
    /// <summary>The process exited with code 0 inside its time limit.</summary>
    Succeeded = 0,

    /// <summary>The process exited with a non-zero code.</summary>
    Failed = 1,

    /// <summary>The time limit expired and the process tree was killed.</summary>
    TimedOut = 2,

    /// <summary>The program could not be started at all.</summary>
    NotStarted = 3,
}

/// <param name="Outcome">What happened.</param>
/// <param name="ExitCode">The exit code, or null when there was none.</param>
/// <param name="StandardError">
/// What the tool wrote to stderr, truncated. For the log and for an incident,
/// never for the end user: a converter's stderr can echo a file path or part
/// of the document.
/// </param>
/// <param name="Duration">Wall-clock time the process ran for.</param>
public sealed record SandboxResult(
    SandboxOutcome Outcome,
    int? ExitCode,
    string StandardError,
    TimeSpan Duration)
{
    public bool Succeeded => Outcome == SandboxOutcome.Succeeded;
}

/// <summary>What a sandboxed process is allowed to do.</summary>
/// <param name="Timeout">
/// Wall-clock limit. When it expires the whole process tree is killed:
/// LibreOffice in particular starts children, and killing only the parent
/// leaves them holding the file.
/// </param>
/// <param name="MemoryLimitBytes">
/// Address-space cap. A malformed document that makes a converter allocate
/// without bound must take down the conversion, not the API.
/// </param>
/// <param name="WorkingDirectory">
/// A directory of its own, deleted by the caller afterwards, so the tool has
/// nowhere else to write.
/// </param>
/// <param name="CpuSeconds">
/// Processor time, as opposed to the wall-clock <paramref name="Timeout"/>: a
/// document that makes a converter spin is stopped by the kernel even when a
/// busy machine would let it run long. Summed over threads, so it is set above
/// the timeout.
/// </param>
public sealed record SandboxLimits(
    TimeSpan Timeout,
    long MemoryLimitBytes,
    string WorkingDirectory,
    int CpuSeconds = 120)
{
    /// <summary>The defaults from plan section 2.2: 60 seconds, 1 GB, and 120 CPU seconds.</summary>
    public static SandboxLimits Default(string workingDirectory) =>
        new(TimeSpan.FromSeconds(60), 1024L * 1024 * 1024, workingDirectory);

    /// <summary>
    /// Someone is waiting for the answer: when the converter container is not
    /// running, report that at once rather than waiting out the timeout.
    /// </summary>
    public bool FailFast { get; init; }
}

/// <summary>
/// Runs a converter as a separate process under a time and memory limit
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// <para>
/// User files are untrusted input and the converters that read them -
/// LibreOffice, Ghostscript - are large C++ programs with a long history of
/// parser bugs. Running them in-process would put that attack surface inside
/// the API. So each conversion is a child process that is given a working
/// directory of its own, a stripped environment, a memory cap and a deadline,
/// and is killed with its children when the deadline passes.
/// </para>
/// <para>
/// On Linux the program is started under <c>prlimit</c> with an address-space
/// and a CPU-time cap. It does not by itself take the network away: that is
/// the converter container's job (<see cref="IsolatedProcessSandbox"/>,
/// <c>network_mode: none</c>), which runs this same class inside it. On a
/// development machine without Docker, <c>Conversion:Mode=Local</c> runs it
/// in the API's own environment - allowed in Development only
/// (<see cref="ConversionOptions.EnsureAllowedIn"/>).
/// </para>
/// </remarks>
public sealed class SandboxedProcess : IProcessSandbox
{
    /// <summary>Stderr kept per run. Enough to diagnose, bounded so a loop cannot fill the log.</summary>
    private const int MaxStandardErrorChars = 8 * 1024;

    private readonly ILogger<SandboxedProcess> _logger;

    public SandboxedProcess(ILogger<SandboxedProcess> logger) => _logger = logger;

    /// <summary>Runs <paramref name="fileName"/> and waits for it, within the limits.</summary>
    public async Task<SandboxResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        SandboxLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(limits);

        // The converter tool is ours and is started through the dotnet host.
        var managed = string.Equals(fileName, ToolContract.Program, StringComparison.Ordinal);

        if (managed)
        {
            if (ConverterToolCommand.Find() is not { } tool)
            {
                _logger.LogError("The converter tool ({Assembly}) is not installed next to the application.", ToolContract.AssemblyFile);

                return new SandboxResult(
                    SandboxOutcome.NotStarted, null, "The converter tool was not found.", TimeSpan.Zero);
            }

            arguments = [tool.Assembly, .. arguments];
            fileName = tool.Host;
        }

        // The limits are applied by the wrapper before exec, so they bind the
        // converter itself rather than a shell that started it.
        var wrapped = false;

        if (LimitCommand(limits, managed) is { } wrapper)
        {
            // Under the wrapper a missing converter would surface as the
            // wrapper's exit code - "the document failed" - rather than as
            // "the converter is not installed". Look it up first, so the
            // user is told the right thing.
            if (FindExecutable(fileName) is null)
            {
                _logger.LogError("The converter {FileName} is not installed.", fileName);

                return new SandboxResult(
                    SandboxOutcome.NotStarted, null, $"{fileName} was not found.", TimeSpan.Zero);
            }

            arguments = [.. wrapper.Prefix, fileName, .. arguments];
            fileName = wrapper.FileName;
            wrapped = true;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = limits.WorkingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Harden(startInfo, limits, managed);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderr.Length < MaxStandardErrorChars)
            {
                stderr.AppendLine(e.Data);
            }
        };

        // Read and discard: a child that fills its stdout pipe blocks forever,
        // and then the timeout is the only thing that ends it.
        process.OutputDataReceived += (_, _) => { };

        try
        {
            if (!process.Start())
            {
                return new SandboxResult(
                    SandboxOutcome.NotStarted, null, "The process did not start.", stopwatch.Elapsed);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not start the converter {FileName}.", fileName);

            return new SandboxResult(
                SandboxOutcome.NotStarted, null, exception.Message, stopwatch.Elapsed);
        }

        ApplyMemoryLimit(process, limits.MemoryLimitBytes);

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // entireProcessTree: soffice forks a child that holds the document
            // open, so killing only the parent leaves the file locked and the
            // work still running.
            Kill(process);

            stopwatch.Stop();

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(
                "The converter {FileName} exceeded its {Seconds}s limit and was killed.",
                fileName,
                limits.Timeout.TotalSeconds);

            return new SandboxResult(
                SandboxOutcome.TimedOut,
                null,
                Truncate(stderr),
                stopwatch.Elapsed);
        }

        stopwatch.Stop();

        return new SandboxResult(
            process.ExitCode switch
            {
                0 => SandboxOutcome.Succeeded,

                // prlimit could not exec the program (126: not executable,
                // 127: not found) - the program never ran.
                126 or 127 when wrapped => SandboxOutcome.NotStarted,
                _ => SandboxOutcome.Failed,
            },
            process.ExitCode,
            Truncate(stderr),
            stopwatch.Elapsed);
    }

    /// <summary>
    /// Strips the environment the child inherits.
    /// </summary>
    /// <remarks>
    /// The API's environment carries the OpenBao address and credentials file
    /// paths, and on a developer machine a proxy configuration as well. A
    /// converter needs none of it, and a converter with a parser bug should
    /// not be handed any of it. Only what the tools genuinely need is put
    /// back.
    /// </remarks>
    private static void Harden(ProcessStartInfo startInfo, SandboxLimits limits, bool managed)
    {
        startInfo.Environment.Clear();

        // A minimum for a process to run at all. No proxy variables, so a tool
        // that would otherwise honour http_proxy has nothing to honour.
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["HOME"] = limits.WorkingDirectory;
        startInfo.Environment["TMPDIR"] = limits.WorkingDirectory;
        startInfo.Environment["TEMP"] = limits.WorkingDirectory;
        startInfo.Environment["TMP"] = limits.WorkingDirectory;

        // Deterministic output: a converter that picks up a locale renders
        // dates and decimal separators differently, and the page count can
        // change with it.
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";

        if (managed)
        {
            // The tool is a .NET process that lives for one file. The collector
            // is told the limit it works under, so it collects rather than
            // running into it; no diagnostic pipes, no first-run output.
            startInfo.Environment["DOTNET_gcServer"] = "0";
            startInfo.Environment["DOTNET_GCHeapHardLimit"] = "0x" + (limits.MemoryLimitBytes / 4 * 3).ToString("X", CultureInfo.InvariantCulture);
            startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows needs these to start a process at all.
            foreach (var name in new[] { "SystemRoot", "windir", "SystemDrive", "COMSPEC", "PATHEXT" })
            {
                var value = Environment.GetEnvironmentVariable(name);

                if (!string.IsNullOrEmpty(value))
                {
                    startInfo.Environment[name] = value;
                }
            }
        }
    }

    /// <summary>
    /// Caps how much memory the child may take.
    /// </summary>
    /// <remarks>
    /// On Linux the limit is applied by the wrapper the caller runs the tool
    /// through (see <see cref="MemoryLimitCommand"/>); there is no portable
    /// way to set another process's rlimit after it has started. On Windows a
    /// Job Object would do it, which step 8 adds along with the rest of the
    /// isolation. Logged rather than silently skipped, so nobody reads this
    /// class as enforcing more than it does.
    /// </remarks>
    private void ApplyMemoryLimit(Process process, long bytes)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug(
                "Process {Pid} runs without a Job Object memory cap; the limit of {Bytes} bytes "
                + "is enforced on Linux by the wrapper and on Windows in step 8.",
                process.Id,
                bytes);
        }
    }

    /// <summary>
    /// The full path a program name resolves to, the way the OS would find
    /// it: an absolute or relative path as it is, a bare name on PATH.
    /// </summary>
    public static string? FindExecutable(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The command that applies the memory and CPU-time limits on this
    /// platform, or null.
    /// </summary>
    /// <remarks>
    /// <c>prlimit</c> is in util-linux and is present in the backend image. It
    /// sets the limits before exec, which is the only way to have them apply
    /// to the tool itself rather than to a shell that started it.
    /// </remarks>
    /// <param name="managed">
    /// The program is the converter tool, a .NET process. The runtime reserves
    /// several gigabytes of address space it never touches, so an
    /// address-space cap would stop it starting; the cap is put on the memory
    /// it can actually write to instead (<c>RLIMIT_DATA</c>), which binds the
    /// native parsers' allocations just the same.
    /// </param>
    public static (string FileName, string[] Prefix)? LimitCommand(SandboxLimits limits, bool managed = false)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var memory = managed ? $"--data={limits.MemoryLimitBytes}" : $"--as={limits.MemoryLimitBytes}";

        return ("prlimit", [memory, $"--cpu={limits.CpuSeconds}", "--"]);
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not kill the converter process tree.");
        }
    }

    private static string Truncate(StringBuilder stderr)
        => stderr.Length <= MaxStandardErrorChars
            ? stderr.ToString()
            : stderr.ToString(0, MaxStandardErrorChars) + "… (truncated)";
}
