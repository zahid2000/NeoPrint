using System.Diagnostics;
using System.Text;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>What the recorder needs from the machine: FFmpeg, the camera, and a process (T-482).</summary>
public interface ICameraHost
{
    /// <summary>Where FFmpeg is, or null when it is not installed.</summary>
    string? FindFfmpeg();

    /// <summary>Runs FFmpeg to ask it something and returns what it printed, both streams.</summary>
    Task<string> AskAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>The camera to record from, or null when none is plugged in.</summary>
    Task<CameraDevice?> FindCameraAsync(string ffmpeg, KioskCameraSettings settings, CancellationToken cancellationToken);

    /// <summary>Starts the recording FFmpeg.</summary>
    IRecorderProcess Start(string ffmpeg, IReadOnlyList<string> arguments);
}

/// <summary>A running FFmpeg.</summary>
public interface IRecorderProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    /// <summary>The last lines FFmpeg wrote to standard error: why it stopped.</summary>
    string ErrorTail { get; }

    /// <summary>The latest live frame FFmpeg gave (T-483), if any.</summary>
    CameraFrame? LatestFrame => null;

    void Stop();
}

/// <summary>
/// The machine's FFmpeg and camera (T-482). On Windows the camera is found
/// through DirectShow, as FFmpeg lists it; on Linux through sysfs.
/// </summary>
public sealed class SystemCameraHost : ICameraHost
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(20);

    private readonly KioskSettings _settings;

    public SystemCameraHost(KioskSettings settings) => _settings = settings;

    public string? FindFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(_settings.FfmpegPath))
        {
            return File.Exists(_settings.FfmpegPath) ? _settings.FfmpegPath : null;
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        // Where provisioning puts it on Windows (desktop/provisioning/windows/provision.ps1).
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NeoPrint", "ffmpeg", "bin", name));
        }

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim('"'), name)));

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<string> AskAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AskTimeout);

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    public async Task<CameraDevice?> FindCameraAsync(string ffmpeg, KioskCameraSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_settings.FakeCamera)
        {
            var (width, height) = settings.Size;
            return CameraDevice.Fake(width, height, settings.FramesPerSecond);
        }

        if (OperatingSystem.IsWindows())
        {
            var listed = FfmpegOutput.DirectShowVideoDevices(
                await AskAsync(ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], cancellationToken).ConfigureAwait(false));

            var name = string.IsNullOrWhiteSpace(_settings.CameraDevice)
                ? listed.FirstOrDefault()
                : listed.FirstOrDefault(device => string.Equals(device, _settings.CameraDevice, StringComparison.OrdinalIgnoreCase));

            return name is null ? null : new CameraDevice(name, "dshow", name);
        }

        var devices = V4l2Devices.Find();
        return string.IsNullOrWhiteSpace(_settings.CameraDevice)
            ? devices.FirstOrDefault()
            : devices.FirstOrDefault(device => device.Input == _settings.CameraDevice || device.Name == _settings.CameraDevice);
    }

    public IRecorderProcess Start(string ffmpeg, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return new RecorderProcess(Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start."));
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

    private sealed class RecorderProcess : IRecorderProcess
    {
        private const int TailLines = 20;

        private readonly Process _process;
        private readonly Queue<string> _tail = new();
        private readonly object _gate = new();

        private CameraFrame? _latest;

        public RecorderProcess(Process process)
        {
            _process = process;
            _process.ErrorDataReceived += (_, line) => Keep(line.Data);
            _process.BeginErrorReadLine();

            // Always read, whoever watches: FFmpeg stops recording when its
            // output is not taken.
            _ = Task.Run(() => ReadFramesAsync(_process.StandardOutput.BaseStream));
        }

        public CameraFrame? LatestFrame => Volatile.Read(ref _latest);

        private async Task ReadFramesAsync(Stream output)
        {
            var splitter = new JpegFrameSplitter();
            var buffer = new byte[65536];

            try
            {
                int read;
                while ((read = await output.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    foreach (var jpeg in splitter.Push(buffer.AsSpan(0, read)))
                    {
                        Volatile.Write(ref _latest, new CameraFrame(jpeg, DateTime.UtcNow));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // FFmpeg stopped; the recorder starts it again.
            }
        }

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode => HasExited ? _process.ExitCode : null;

        public string ErrorTail
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _tail);
                }
            }
        }

        /// <summary>
        /// Killed, not asked: the files are fragmented MP4, so the minute
        /// being written plays up to the moment it stopped.
        /// </summary>
        public void Stop()
        {
            if (!HasExited)
            {
                Kill(_process);
                _process.WaitForExit(5000);
            }
        }

        public void Dispose()
        {
            Stop();
            _process.Dispose();
        }

        private void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_gate)
            {
                _tail.Enqueue(line.Length > 300 ? line[..300] : line);
                while (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
                }
            }
        }
    }
}

/// <summary>Writes and reads the id of the FFmpeg the kiosk started, so one left behind by a crash is stopped.</summary>
public static class RecorderPidFile
{
    public static void Write(string path, int pid)
    {
        try
        {
            File.WriteAllText(path, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.ASCII);
        }
        catch (IOException)
        {
            // Only a help after a crash.
        }
    }

    /// <summary>Stops the FFmpeg named in the file, if it is still running and is an FFmpeg; then forgets it.</summary>
    public static void StopLeftover(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (int.TryParse(File.ReadAllText(path).Trim(), out var pid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(5000);
                    }
                }
                catch (ArgumentException)
                {
                    // Not running.
                }
                catch (InvalidOperationException)
                {
                    // Gone in between.
                }
            }

            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
