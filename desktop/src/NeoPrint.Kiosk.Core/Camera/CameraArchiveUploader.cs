using System.Net;

using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>
/// Sends each sealed minute to the backend, for the file server the panel
/// chose (T-485), oldest first. The kiosk keeps its own copy until its own
/// time; one the backend already has is answered as stored, so a lost answer
/// is simply sent again.
/// </summary>
/// <remarks>
/// The kiosk holds no credential for the file server: it sends to the
/// backend, the backend writes. What it sends is already sealed.
/// </remarks>
public sealed class CameraArchiveUploader
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    /// <summary>After the backend could not be reached: not every fifteen seconds.</summary>
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(1);

    /// <summary>At most this many minutes in a round, so a long backlog does not hold the line.</summary>
    public const int PerRound = 20;

    private readonly CameraVault _vault;
    private readonly Func<KioskCameraSettings?> _settings;
    private readonly Func<SealedSegment, CancellationToken, Task> _send;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private DateTime _nextTry = DateTime.MinValue;
    private int _unsent = -1;
    private long _lastSentTicks;

    /// <param name="send">Sends one sealed minute (<c>POST kiosks/me/camera/segments</c>).</param>
    public CameraArchiveUploader(
        CameraVault vault,
        Func<KioskCameraSettings?> settings,
        Func<SealedSegment, CancellationToken, Task> send,
        IClock clock,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _vault = vault;
        _settings = settings;
        _send = send;
        _clock = clock;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Minutes still to send; null while nothing goes to a file server.</summary>
    public int? Unsent => Volatile.Read(ref _unsent) is >= 0 and var count ? count : null;

    public DateTime? LastSentAt => Interlocked.Read(ref _lastSentTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "The camera archive could not read a minute; it tries again.");
            }

            await _delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One round: sends what waits, up to <see cref="PerRound"/>.</summary>
    /// <returns>How many minutes reached the file server.</returns>
    public async Task<int> StepAsync(CancellationToken cancellationToken)
    {
        if (_settings() is not { Archive: true })
        {
            Volatile.Write(ref _unsent, -1);
            return 0;
        }

        var waiting = _vault.Unsent();
        Volatile.Write(ref _unsent, waiting.Count);

        if (waiting.Count == 0 || _clock.UtcNow < _nextTry)
        {
            return 0;
        }

        var sent = 0;

        foreach (var segment in waiting.Take(PerRound))
        {
            try
            {
                await _send(segment, cancellationToken).ConfigureAwait(false);
                _vault.MarkSent(segment);
                sent++;
                Interlocked.Exchange(ref _lastSentTicks, _clock.UtcNow.Ticks);
            }
            catch (KioskApiException exception) when (exception.Code == "camera.archiveOff")
            {
                // Turned off since the configuration came: it arrives again shortly.
                _nextTry = _clock.UtcNow + AfterFailure;
                break;
            }
            catch (KioskApiException exception) when (exception.Status == HttpStatusCode.Conflict)
            {
                // The backend will never take this one (its time, its form): it
                // stays on the kiosk, and the others go on.
                _logger.LogWarning("The file server refused a camera minute ({Code}); it stays on the kiosk only.", exception.Code);
                _vault.MarkSent(segment);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or KioskApiException
                && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("The camera archive could not reach the backend; it tries again in a minute.");
                _nextTry = _clock.UtcNow + AfterFailure;
                break;
            }
        }

        Volatile.Write(ref _unsent, Math.Max(0, waiting.Count - sent));
        return sent;
    }
}
