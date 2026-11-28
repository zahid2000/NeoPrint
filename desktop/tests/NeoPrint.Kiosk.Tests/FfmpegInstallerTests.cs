using System.IO.Compression;
using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The agent installs the camera's FFmpeg itself (T-496): only when the kiosk
/// asks, only the pinned file, unpacked where the camera looks.
/// </summary>
public sealed class FfmpegInstallerTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-ffmpeg-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Start);
    private readonly byte[] _zip;
    private int _downloads;

    public FfmpegInstallerTests()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("ffmpeg-n8.1-win64-lgpl-8.1/bin/ffmpeg.exe");
            using (var writer = entry.Open())
            {
                writer.Write("MZ fake ffmpeg"u8);
            }

            archive.CreateEntry("ffmpeg-n8.1-win64-lgpl-8.1/LICENSE.txt");
        }

        _zip = buffer.ToArray();
    }

    private string Install => Path.Combine(_root, "Program Files", "NeoPrint");

    private string Camera => Path.Combine(_root, "ProgramData", "NeoPrint", "camera");

    private FfmpegPin Pin(byte[]? expected = null)
        => new(new Uri("https://example.test/ffmpeg.zip"), Convert.ToHexStringLower(SHA256.HashData(expected ?? _zip)), (expected ?? _zip).Length);

    private FfmpegInstaller Installer(FfmpegPin pin, byte[]? served = null) => new(
        Install,
        Camera,
        pin,
        async (_, target, ct) =>
        {
            _downloads++;
            await target.WriteAsync(served ?? _zip, ct);
        },
        _clock,
        NullLogger.Instance);

    private void Ask() => FfmpegSetup.Ask(Camera);

    [Fact]
    public async Task Nothing_is_downloaded_until_the_kiosk_asks()
    {
        (await Installer(Pin()).StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        _downloads.Should().Be(0);
    }

    [Fact]
    public async Task Asked_the_pinned_build_is_unpacked_where_the_camera_looks_and_the_ask_forgotten()
    {
        Ask();
        var installer = Installer(Pin());

        (await installer.StepAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        File.ReadAllText(FfmpegSetup.Executable(Install)).Should().Be("MZ fake ffmpeg");
        File.Exists(Path.Combine(Camera, FfmpegSetup.WantedFile)).Should().BeFalse();
        Directory.GetFileSystemEntries(Install).Select(Path.GetFileName).Should().Equal("ffmpeg");

        // Asked again while it is there: nothing downloaded.
        Ask();
        (await installer.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        _downloads.Should().Be(1);
        File.Exists(Path.Combine(Camera, FfmpegSetup.WantedFile)).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_that_is_not_the_pinned_one_is_never_unpacked_and_not_fetched_again_soon()
    {
        Ask();
        var other = _zip.ToArray();
        other[^1] ^= 0xFF;
        var installer = Installer(Pin(), served: other);

        (await installer.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        File.Exists(FfmpegSetup.Executable(Install)).Should().BeFalse();
        Directory.GetFileSystemEntries(Install).Should().BeEmpty("nothing is left behind");

        _clock.Now += TimeSpan.FromMinutes(30);
        await installer.StepAsync(TestContext.Current.CancellationToken);
        _downloads.Should().Be(1, "a mismatch is tried again only hours later");

        _clock.Now += FfmpegInstaller.AfterMismatch;
        await installer.StepAsync(TestContext.Current.CancellationToken);
        _downloads.Should().Be(2);
    }

    [Fact]
    public async Task A_larger_answer_is_cut_off_and_tried_again_later()
    {
        Ask();
        var installer = Installer(Pin(), served: [.. _zip, .. new byte[100]]);

        (await installer.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        File.Exists(FfmpegSetup.Executable(Install)).Should().BeFalse();

        _clock.Now += FfmpegInstaller.AfterFailure;
        await installer.StepAsync(TestContext.Current.CancellationToken);
        _downloads.Should().Be(2);
    }

    [Fact]
    public async Task The_kiosk_asks_the_agent_when_the_camera_is_on_and_ffmpeg_is_missing()
    {
        var vault = new CameraVault(Camera, _clock, NullLogger.Instance, _ => 100L * 1024 * 1024 * 1024, TimeZoneInfo.Utc);
        var publicKey = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo());
        var recorder = new CameraRecorder(
            vault,
            new NoFfmpegHost(),
            () => new KioskCameraSettings(KioskCameraSettings.Activity, 2, 20, "1280x720", 10, "cam-test", publicKey),
            () => false,
            _clock,
            NullLogger.Instance,
            askForFfmpeg: true);

        await recorder.StepAsync(TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(Camera, FfmpegSetup.WantedFile)).Should().BeTrue();
        recorder.Status.Error.Should().Be(CameraError.NoRecorder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoFfmpegHost : ICameraHost
    {
        public string? FindFfmpeg() => null;

        public Task<string> AskAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => throw new InvalidOperationException();

        public Task<CameraDevice?> FindCameraAsync(string ffmpeg, KioskCameraSettings settings, CancellationToken cancellationToken)
            => throw new InvalidOperationException();

        public IRecorderProcess Start(string ffmpeg, IReadOnlyList<string> arguments) => throw new InvalidOperationException();
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime Now { get; set; } = now;

        public DateTime UtcNow => Now;
    }
}
