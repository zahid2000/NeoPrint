using System.Net;

using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>One live JPEG from FFmpeg and when it came (T-483).</summary>
public sealed record CameraFrame(byte[] Jpeg, DateTime At);

/// <summary>A span of recorded minutes the panel asked for (<c>GET kiosks/me/camera/requests</c>).</summary>
public sealed record CameraFootageRequest(Guid Id, DateTime From, DateTime To);

/// <summary>
/// Cuts FFmpeg's MJPEG output - one JPEG after another - into frames: each
/// starts FF D8 and ends FF D9 (inside the picture an FF is always followed
/// by 00 or a restart marker, never D9).
/// </summary>
public sealed class JpegFrameSplitter
{
    /// <summary>A "frame" larger than this is not one: what was gathered is dropped.</summary>
    public const int MaxFrameBytes = 1024 * 1024;

    private readonly MemoryStream _frame = new();
    private bool _inFrame;
    private byte _previous;

    /// <summary>The frames these bytes finish.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        List<byte[]>? frames = null;

        foreach (var b in bytes)
        {
            if (!_inFrame)
            {
                if (_previous == 0xFF && b == 0xD8)
                {
                    _inFrame = true;
                    _frame.SetLength(0);
                    _frame.WriteByte(0xFF);
                    _frame.WriteByte(0xD8);
                }

                _previous = b;
                continue;
            }

            _frame.WriteByte(b);

            if (_previous == 0xFF && b == 0xD9)
            {
                (frames ??= []).Add(_frame.ToArray());
                _inFrame = false;
                _previous = 0;
                continue;
            }

            if (_frame.Length > MaxFrameBytes)
            {
                _inFrame = false;
            }

            _previous = b;
        }

        return frames ?? (IReadOnlyList<byte[]>)[];
    }
}

/// <summary>
/// The live view (T-483): when the panel starts watching, the kiosk sends
/// its latest frame about twice a second, until the backend says nobody
/// watches any more. Nobody watching, nothing leaves the kiosk.
/// </summary>
public sealed class CameraLiveSender
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>A frame older than this is not sent: FFmpeg is not giving any.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);

    /// <summary>With no frame for this long - the camera is not recording - the kiosk stops trying.</summary>
    public static readonly TimeSpan GiveUpWithoutFrames = TimeSpan.FromSeconds(30);

    /// <summary>Failures in a row before the kiosk stops; the panel asks again if it still watches.</summary>
    public const int MaxFailures = 5;

    private readonly Func<CameraFrame?> _latest;
    private readonly Func<byte[], CancellationToken, Task<bool>> _send;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private int _running;

    /// <param name="send">Sends one frame; answers whether somebody still watches.</param>
    public CameraLiveSender(
        Func<CameraFrame?> latest,
        Func<byte[], CancellationToken, Task<bool>> send,
        IClock clock,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _latest = latest;
        _send = send;
        _clock = clock;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Somebody started watching: sends until told to stop. Asked while sending, nothing more happens.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var started = _clock.UtcNow;
            DateTime? lastSent = null;
            var failures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _clock.UtcNow;
                var frame = _latest();

                if (frame is not null && now - frame.At <= Stale && frame.At != lastSent)
                {
                    try
                    {
                        if (!await _send(frame.Jpeg, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        lastSent = frame.At;
                        failures = 0;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or KioskApiException
                        or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        if (++failures >= MaxFailures)
                        {
                            _logger.LogInformation("The live camera view could not reach the backend; it stops until asked again.");
                            return;
                        }
                    }
                }
                else if (now - (lastSent ?? started) > GiveUpWithoutFrames)
                {
                    _logger.LogInformation("The live camera view has no frames (the camera is not recording); it stops.");
                    return;
                }

                await _delay(Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}

/// <summary>
/// Sends the recorded minutes the panel asks for (T-483): asked over the
/// hub, and looked for now and then - a kiosk offline when asked answers
/// when it is back. Only minutes of the span asked for, sealed as they are.
/// </summary>
public sealed class CameraFootageSender
{
    public static readonly TimeSpan Poll = TimeSpan.FromMinutes(2);

    private readonly CameraVault _vault;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CameraFootageRequest>>> _requests;
    private readonly Func<Guid, SealedSegment, CancellationToken, Task> _send;
    private readonly Func<Guid, int, CancellationToken, Task> _complete;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CameraFootageSender(
        CameraVault vault,
        Func<CancellationToken, Task<IReadOnlyList<CameraFootageRequest>>> requests,
        Func<Guid, SealedSegment, CancellationToken, Task> send,
        Func<Guid, int, CancellationToken, Task> complete,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _vault = vault;
        _requests = requests;
        _send = send;
        _complete = complete;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await StepAsync(cancellationToken).ConfigureAwait(false);
            await _delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Answers every open request; one round at a time.</summary>
    /// <returns>How many minutes were sent.</returns>
    public async Task<int> StepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CameraFootageRequest> open;
            try
            {
                open = await _requests(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or KioskApiException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            var total = 0;
            foreach (var request in open)
            {
                var sent = 0;
                var reached = true;

                foreach (var minute in _vault.Between(request.From, request.To))
                {
                    try
                    {
                        await _send(request.Id, minute, cancellationToken).ConfigureAwait(false);
                        sent++;
                    }
                    catch (KioskApiException exception) when (exception.Status == HttpStatusCode.Conflict)
                    {
                        // The backend will not take this one (its time, its form): the others go on.
                        _logger.LogWarning("The backend refused a requested camera minute ({Code}).", exception.Code);
                    }
                    catch (FileNotFoundException)
                    {
                        // Pruned in the meantime.
                    }
                    catch (Exception exception) when (exception is HttpRequestException or KioskApiException
                        or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        // Unreachable: the request stays open, and the next round sends it again.
                        reached = false;
                        break;
                    }
                }

                if (!reached)
                {
                    break;
                }

                try
                {
                    await _complete(request.Id, sent, cancellationToken).ConfigureAwait(false);
                    total += sent;
                }
                catch (Exception exception) when (exception is HttpRequestException or KioskApiException
                    or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return total;
        }
        finally
        {
            _gate.Release();
        }
    }
}
