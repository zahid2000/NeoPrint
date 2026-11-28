using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>Why the camera does not record, as telemetry reports it (backend <c>CameraErrors</c>).</summary>
public static class CameraError
{
    public const string NoCamera = "noCamera";
    public const string NoRecorder = "noRecorder";
    public const string NoKey = "noKey";
    public const string RecorderFailed = "recorderFailed";
    public const string DiskFull = "diskFull";
}

/// <summary>The camera as telemetry reports it and the screen shows it.</summary>
/// <param name="Connected">A camera was found; null before the first look.</param>
public sealed record CameraStatus(
    bool Enabled,
    bool? Connected,
    bool Recording,
    string? Mode,
    string? Error,
    CameraStorage Storage)
{
    public static readonly CameraStatus Off = new(false, null, false, null, null, new CameraStorage(0, 0, null, null));
}

/// <summary>
/// Keeps FFmpeg recording while the camera is turned on (T-482): finds
/// FFmpeg, an encoder and the camera; starts it, and again when it stops;
/// seals each finished minute and keeps the disk within its limits.
/// </summary>
/// <remarks>
/// FFmpeg runs as a separate process - the camera, the encoder and their
/// licences stay out of the kiosk application - and the kiosk application
/// records, not the agent: it is the one that holds the configuration, and
/// it runs in the session that has the camera. A crash leaves FFmpeg behind;
/// its id is kept in a file, and the next start stops it before the camera
/// is opened again.
/// </remarks>
public sealed class CameraRecorder
{
    /// <summary>How often the recorder looks.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    /// <summary>How often the old recordings are deleted.</summary>
    public static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(5);

    /// <summary>FFmpeg that stops sooner than this did not get going.</summary>
    public static readonly TimeSpan QuickExit = TimeSpan.FromSeconds(20);

    /// <summary>Failures in a row before the panel is told the recorder keeps stopping.</summary>
    public const int FailuresReported = 3;

    private static readonly TimeSpan NoCameraRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoRecorderRetry = TimeSpan.FromMinutes(10);

    private readonly CameraVault _vault;
    private readonly ICameraHost _host;
    private readonly Func<KioskCameraSettings?> _settings;
    private readonly Func<bool> _busy;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ActivityLog _activity = new();

    private IRecorderProcess? _process;
    private KioskCameraSettings? _running;
    private DateTime _startedAt;
    private DateTime _nextStart = DateTime.MinValue;
    private DateTime _nextPrune = DateTime.MinValue;
    private int _failures;
    private bool _askSize = true;
    private bool? _connected;
    private string? _error;
    private (string Ffmpeg, string? Encoder)? _encoder;
    private CameraStatus _status = CameraStatus.Off;

    /// <param name="settings">The configuration's camera section; null while the camera is turned off.</param>
    /// <param name="busy">Whether somebody is at the kiosk or it is printing: what <c>Activity</c> mode keeps.</param>
    public CameraRecorder(
        CameraVault vault,
        ICameraHost host,
        Func<KioskCameraSettings?> settings,
        Func<bool> busy,
        IClock clock,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _vault = vault;
        _host = host;
        _settings = settings;
        _busy = busy;
        _clock = clock;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The camera now: read by telemetry and the screen.</summary>
    public CameraStatus Status => Volatile.Read(ref _status);

    /// <summary>The latest live frame (T-483), while FFmpeg runs.</summary>
    public CameraFrame? LatestFrame => Volatile.Read(ref _process)?.LatestFrame;

    /// <summary>Recording started or stopped: the screen shows its sign while it records.</summary>
    public event Action<bool>? RecordingChanged;

    private string PidFile => Path.Combine(_vault.Root, "ffmpeg.pid");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _vault.Prepare();
        RecorderPidFile.StopLeftover(PidFile);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await StepAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception)
                {
                    _logger.LogWarning(exception, "The camera recorder stumbled; it looks again shortly.");
                }

                await _delay(Poll, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One look: what the recorder should be doing now, and doing it.</summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var settings = _settings();
        _activity.Mark(_busy(), now);

        if (settings is null)
        {
            StopProcess();
            _vault.DiscardWriting();
            _connected = null;
            _error = null;
            Publish(settings, PruneIfDue(settings, now));
            return;
        }

        if (!CameraVault.CanSeal(settings))
        {
            // Nothing may be kept in the clear: no key, no recording.
            StopProcess();
            _vault.DiscardWriting();
            _error = CameraError.NoKey;
            Publish(settings, PruneIfDue(settings, now));
            return;
        }

        // Another size or rate: FFmpeg starts again with it.
        if (_process is not null && _running is { } running && (running.Size != settings.Size || running.FramesPerSecond != settings.FramesPerSecond
            || running.QualityLevel != settings.QualityLevel))
        {
            StopProcess();
            _nextStart = now;
            _askSize = true;
        }

        if (_process is { HasExited: true } exited)
        {
            Exited(exited, now);
        }

        var storage = PruneIfDue(settings, now);

        if (!_vault.HasRoom)
        {
            StopProcess();
            storage = _vault.Prune(settings);

            if (!_vault.HasRoom)
            {
                _error = CameraError.DiskFull;
                await _vault.SealAsync(settings, _activity, writing: false, cancellationToken).ConfigureAwait(false);
                Publish(settings, storage);
                return;
            }
        }

        if (_process is null && now >= _nextStart)
        {
            await TryStartAsync(settings, now, cancellationToken).ConfigureAwait(false);
        }

        await _vault.SealAsync(settings, _activity, writing: _process is not null, cancellationToken).ConfigureAwait(false);
        Publish(settings, _vault.Storage);
    }

    /// <summary>Stops FFmpeg and seals what it wrote, whatever the mode: the last minutes before a restart are kept.</summary>
    public async Task StopAsync()
    {
        StopProcess();

        if (_settings() is { } settings && CameraVault.CanSeal(settings))
        {
            await _vault.SealAsync(settings with { Mode = KioskCameraSettings.Continuous }, _activity, writing: false, CancellationToken.None)
                .ConfigureAwait(false);
        }

        Publish(_settings(), _vault.Storage);
    }

    private async Task TryStartAsync(KioskCameraSettings settings, DateTime now, CancellationToken cancellationToken)
    {
        var ffmpeg = _host.FindFfmpeg();
        if (ffmpeg is null)
        {
            _error = CameraError.NoRecorder;
            _nextStart = now + NoRecorderRetry;
            return;
        }

        if (_encoder is not { } known || known.Ffmpeg != ffmpeg)
        {
            known = (ffmpeg, FfmpegOutput.PickEncoder(
                await _host.AskAsync(ffmpeg, ["-hide_banner", "-encoders"], cancellationToken).ConfigureAwait(false)));
            _encoder = known;
        }

        if (known.Encoder is not { } encoder)
        {
            _logger.LogWarning("FFmpeg at {Path} has none of the encoders the camera may use ({Encoders}).", ffmpeg, string.Join(", ", FfmpegOutput.Encoders));
            _error = CameraError.NoRecorder;
            _nextStart = now + NoRecorderRetry;
            return;
        }

        var device = await _host.FindCameraAsync(ffmpeg, settings, cancellationToken).ConfigureAwait(false);
        _connected = device is not null;

        if (device is null)
        {
            _error = CameraError.NoCamera;
            _nextStart = now + NoCameraRetry;
            return;
        }

        _process = _host.Start(ffmpeg, FfmpegCommand.Record(device, encoder, settings, _vault.WritingPattern, _askSize));
        _running = settings;
        _startedAt = now;
        RecorderPidFile.Write(PidFile, _process.Id);

        _logger.LogInformation(
            "The camera records from {Camera} with {Encoder}, {Resolution} at {Fps} fps.", device.Name, encoder, settings.Resolution, settings.FramesPerSecond);
    }

    private void Exited(IRecorderProcess process, DateTime now)
    {
        var lasted = now - _startedAt;
        _logger.LogWarning(
            "The camera recorder stopped after {Seconds} s with code {Code}: {Error}",
            (int)lasted.TotalSeconds, process.ExitCode, process.ErrorTail);

        process.Dispose();
        _process = null;

        if (lasted >= QuickExit)
        {
            // It ran: whatever stopped it, start again at once.
            _failures = 0;
            _nextStart = now;
            return;
        }

        if (_askSize)
        {
            // The camera may not give that size or rate: take what it gives, and scale.
            _askSize = false;
            _nextStart = now;
            return;
        }

        _failures++;
        _nextStart = now + (_failures switch
        {
            1 => TimeSpan.FromSeconds(15),
            2 => TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromMinutes(5),
        });

        if (_failures >= FailuresReported)
        {
            _error = CameraError.RecorderFailed;
        }
    }

    private CameraStorage PruneIfDue(KioskCameraSettings? settings, DateTime now)
    {
        if (now < _nextPrune)
        {
            return _vault.Storage;
        }

        _nextPrune = now + PruneEvery;
        return _vault.Prune(settings);
    }

    private void StopProcess()
    {
        if (_process is null)
        {
            return;
        }

        _process.Dispose();
        _process = null;
        _running = null;

        try
        {
            File.Delete(PidFile);
        }
        catch (IOException)
        {
        }
    }

    private void Publish(KioskCameraSettings? settings, CameraStorage storage)
    {
        // Recording once FFmpeg has run past the moment a bad start fails: a
        // recorder that keeps falling over never looks like one that works.
        var recording = _process is { HasExited: false } && _clock.UtcNow - _startedAt >= QuickExit;
        if (recording)
        {
            _error = null;
        }

        var status = settings is null
            ? CameraStatus.Off with { Storage = storage }
            : new CameraStatus(true, _connected, recording, settings.Mode, recording ? null : _error, storage);

        var before = Interlocked.Exchange(ref _status, status);
        if (before.Recording != status.Recording)
        {
            RecordingChanged?.Invoke(status.Recording);
        }
    }
}
