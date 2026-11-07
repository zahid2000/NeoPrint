using NeoPrint.Kiosk.Core;

namespace NeoPrint.Kiosk.Agent;

/// <summary>
/// <c>set-service-pin [file]</c> (T-334): the agent binary sets the machine's
/// service PIN, read from standard input - never an argument, which other
/// users of the machine could see. Run as root by the provisioning scripts
/// (<c>neoprint-service-pin</c>, <c>provision.ps1</c>), which give the file
/// its owner and group.
/// </summary>
public static class ServicePinCommand
{
    /// <returns>0 written, 2 not a PIN (nothing written), 64 not understood, 1 could not write.</returns>
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length > 2)
        {
            await error.WriteLineAsync("Usage: set-service-pin [file] - the PIN on standard input").ConfigureAwait(false);
            return 64;
        }

        var path = args.Length == 2 ? args[1] : ServicePin.DefaultPath;
        var pin = (await input.ReadLineAsync().ConfigureAwait(false))?.Trim();

        if (!ServicePin.IsValid(pin))
        {
            await error.WriteLineAsync($"Not set: a service PIN is {ServicePin.MinLength} to {ServicePin.MaxLength} digits.").ConfigureAwait(false);
            return 2;
        }

        try
        {
            ServicePin.Write(path, pin!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Not set: {path} could not be written ({exception.GetType().Name}).").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"service PIN set: {path}").ConfigureAwait(false);
        return 0;
    }
}
