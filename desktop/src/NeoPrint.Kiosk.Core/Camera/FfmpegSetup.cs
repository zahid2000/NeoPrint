using System.IO.Compression;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>
/// The FFmpeg a Windows kiosk's camera records with (T-482, T-496), pinned:
/// the address, the SHA-256 and the size are compiled into the signed agent,
/// so nothing else - a changed file, another address - is ever installed.
/// </summary>
/// <remarks>
/// BtbN's LGPL build. A month's last build is kept by BtbN for years (the
/// daily ones only days), so the address stays. A new pin comes with an agent
/// release (docs/KIOSK_CAMERA.md §4a).
/// </remarks>
public sealed record FfmpegPin(Uri Address, string Sha256, long Size)
{
    public static readonly FfmpegPin Windows = new(
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n8.1.3-27-g8dca1586f4-win64-lgpl-8.1.zip"),
        "d4c2a42673917b4a97104a3e9f082729a9730796b98664e37c0b7eba6c6c613d",
        167_778_744);
}

/// <summary>Where the kiosk application and the agent meet about FFmpeg (T-496).</summary>
public static class FfmpegSetup
{
    /// <summary>
    /// Written by the kiosk application into the camera directory when the
    /// camera is on and FFmpeg is missing; the agent - which may write to
    /// Program Files - installs it and deletes the file.
    /// </summary>
    public const string WantedFile = "ffmpeg.wanted";

    /// <summary>Where provisioning and the agent put FFmpeg, and the kiosk looks for it first.</summary>
    public static string Directory(string installRoot) => Path.Combine(installRoot, "ffmpeg");

    public static string Executable(string installRoot) => Path.Combine(Directory(installRoot), "bin", "ffmpeg.exe");

    /// <summary>The kiosk application asks for FFmpeg; a failure only means the agent is not asked.</summary>
    public static void Ask(string cameraDirectory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(cameraDirectory);
            var path = Path.Combine(cameraDirectory, WantedFile);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "FFmpeg is missing; the agent installs it (T-496).");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The agent's side (T-496): when the kiosk application asks, downloads the
/// pinned FFmpeg, checks its SHA-256 and size, and unpacks it where the camera
/// looks. Nothing that does not match is unpacked.
/// </summary>
public sealed class FfmpegInstaller
{
    /// <summary>After a download that failed: the network, the address.</summary>
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(15);

    /// <summary>After a file that is not the pinned one: not every quarter of an hour.</summary>
    public static readonly TimeSpan AfterMismatch = TimeSpan.FromHours(6);

    private readonly string _installRoot;
    private readonly string _cameraDirectory;
    private readonly FfmpegPin _pin;
    private readonly Func<Uri, Stream, CancellationToken, Task> _download;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private DateTime _nextTry = DateTime.MinValue;

    /// <param name="download">Writes what the address gives into the stream.</param>
    public FfmpegInstaller(
        string installRoot,
        string cameraDirectory,
        FfmpegPin pin,
        Func<Uri, Stream, CancellationToken, Task> download,
        IClock clock,
        ILogger logger)
    {
        _installRoot = installRoot;
        _cameraDirectory = cameraDirectory;
        _pin = pin;
        _download = download;
        _clock = clock;
        _logger = logger;
    }

    public string ExecutablePath => FfmpegSetup.Executable(_installRoot);

    private string WantedPath => Path.Combine(_cameraDirectory, FfmpegSetup.WantedFile);

    /// <summary>One look: installs FFmpeg when asked and it is missing.</summary>
    /// <returns>Whether FFmpeg was installed now.</returns>
    public async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ExecutablePath))
        {
            Forget();
            return false;
        }

        if (!File.Exists(WantedPath) || _clock.UtcNow < _nextTry)
        {
            return false;
        }

        var unpacking = Path.Combine(_installRoot, "ffmpeg.unpacking");
        var archive = Path.Combine(_installRoot, "ffmpeg.download");

        try
        {
            Delete(unpacking);
            System.IO.Directory.CreateDirectory(_installRoot);

            _logger.LogInformation("The camera has no FFmpeg; the agent downloads the pinned build from {Address}.", _pin.Address);

            await using (var file = new FileStream(archive, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                await using var limited = new LimitedStream(file, _pin.Size);
                await _download(_pin.Address, limited, cancellationToken).ConfigureAwait(false);
                await limited.FlushAsync(cancellationToken).ConfigureAwait(false);

                file.Position = 0;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                if (file.Length != _pin.Size || !string.Equals(hash, _pin.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "The downloaded FFmpeg is not the pinned one ({Bytes} bytes, SHA-256 {Hash}); it is not installed.", file.Length, hash);
                    _nextTry = _clock.UtcNow + AfterMismatch;
                    return false;
                }
            }

            // Entries reaching outside the folder are refused by the unpacker itself.
            ZipFile.ExtractToDirectory(archive, unpacking, overwriteFiles: true);

            var root = System.IO.Directory.EnumerateDirectories(unpacking)
                .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "bin", "ffmpeg.exe")))
                ?? throw new InvalidDataException("The FFmpeg archive has no bin\\ffmpeg.exe.");

            var target = FfmpegSetup.Directory(_installRoot);
            Delete(target);
            System.IO.Directory.Move(root, target);

            _logger.LogInformation("FFmpeg is installed at {Path}; the camera starts within a minute.", ExecutablePath);
            Forget();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "FFmpeg could not be installed; the agent tries again in {Minutes} minutes.", AfterFailure.TotalMinutes);
            _nextTry = _clock.UtcNow + AfterFailure;
            return false;
        }
        finally
        {
            Delete(unpacking);
            DeleteFile(archive);
        }
    }

    private void Forget() => DeleteFile(WantedPath);

    private static void Delete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Refuses to take more than the pinned size: a larger answer is not the file.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count(count);
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Count(int count)
        {
            _written += count;
            if (_written > limit)
            {
                throw new InvalidDataException("The download is larger than the pinned FFmpeg.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            // The file is the caller's.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
