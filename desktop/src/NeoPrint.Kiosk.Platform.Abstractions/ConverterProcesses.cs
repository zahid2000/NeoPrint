using System.ComponentModel;
using System.Diagnostics;

namespace NeoPrint.Kiosk.Platform.Abstractions;

/// <summary>
/// Starting a converter and holding it to its time limit, the part every
/// platform shares (<see cref="IConverterProcessRunner"/>).
/// </summary>
public static class ConverterProcesses
{
    /// <param name="onStarted">Where a platform puts its limits on the new process.</param>
    public static async Task<ConverterRun> RunAsync(
        ProcessStartInfo start,
        TimeSpan timeout,
        Action<Process>? onStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        start.UseShellExecute = false;
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.CreateNoWindow = true;

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                return new ConverterRun(ConverterRunOutcome.NotStarted, -1, string.Empty);
            }
        }
        catch (Win32Exception)
        {
            // Not installed, or not where the configuration says.
            return new ConverterRun(ConverterRunOutcome.NotStarted, -1, string.Empty);
        }

        onStarted?.Invoke(process);

        // Read both streams as they come, or a chatty converter fills its pipe
        // and waits for ever.
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ConverterRun(ConverterRunOutcome.TimedOut, -1, string.Empty);
        }

        var stderr = await error.ConfigureAwait(false);

        // What a converter writes to stderr can quote the document; a caller
        // logs a bounded piece of it at most.
        return new ConverterRun(ConverterRunOutcome.Exited, process.ExitCode, stderr.Length > 2000 ? stderr[..2000] : stderr);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// A converter in a plain child process with a time limit: developer machines
/// and tests. A kiosk uses its platform's runner, which adds the memory limit.
/// </summary>
public sealed class PlainConverterRunner : IConverterProcessRunner
{
    public Task<ConverterRun> RunAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return ConverterProcesses.RunAsync(start, timeout, onStarted: null, cancellationToken);
    }
}
