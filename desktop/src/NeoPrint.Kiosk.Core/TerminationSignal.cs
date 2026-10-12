using System.Runtime.InteropServices;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The operating system asking the kiosk to stop (SIGTERM): systemd
/// restarting or stopping the service, the Agent, a shutdown. The kiosk
/// stops as before - this only lets it note that it was asked to.
/// </summary>
public static class TerminationSignal
{
    /// <summary>
    /// Runs <paramref name="noted"/> when the process is asked to stop. The
    /// request itself is not cancelled: the process goes on to exit.
    /// </summary>
    /// <returns>The registration; keep it for as long as the note matters.</returns>
    public static IDisposable OnRequested(Action noted)
    {
        ArgumentNullException.ThrowIfNull(noted);

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Note(noted));
    }

    /// <summary>The note must never be what keeps the kiosk from stopping.</summary>
    public static void Note(Action noted)
    {
        ArgumentNullException.ThrowIfNull(noted);

        try
        {
            noted();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The disk is going away with the machine: there is nothing to
            // write the note on, and nobody to tell.
        }
    }
}
