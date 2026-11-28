using System.Diagnostics;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The camera's command line against a real FFmpeg (T-496), when one is given
/// with <c>NEOPRINT_FFMPEG</c>: the test picture is recorded as the kiosk
/// would, and the live frames come out beside it. Skipped otherwise.
/// </summary>
public sealed class RealFfmpegTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-real-ffmpeg-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_kiosks_command_records_and_gives_live_frames_with_the_ffmpeg_it_has()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("NEOPRINT_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            Assert.Skip("NEOPRINT_FFMPEG is not set to an FFmpeg executable.");
        }

        var host = new SystemCameraHost(new KioskSettings { FfmpegPath = ffmpeg, FakeCamera = true });
        var encoder = FfmpegOutput.PickEncoder(await host.AskAsync(ffmpeg, ["-hide_banner", "-encoders"], CancellationToken.None));
        var mpdecimate = FfmpegOutput.HasFilter(await host.AskAsync(ffmpeg, ["-hide_banner", "-filters"], CancellationToken.None), "mpdecimate");
        encoder.Should().NotBeNull();

        Directory.CreateDirectory(_root);
        var settings = new KioskCameraSettings(KioskCameraSettings.Continuous, 2, 20, "1280x720", 10, "cam-test", "key");
        var args = FfmpegCommand.Record(
            CameraDevice.Fake(1280, 720, 10), encoder!, settings, Path.Combine(_root, "seg-%Y%m%dT%H%M%S.mp4"), askSize: true, mpdecimate);

        using var process = host.Start(ffmpeg, args);
        await Task.Delay(TimeSpan.FromSeconds(5));
        var frame = process.LatestFrame;
        process.Stop();

        process.ErrorTail.Should().BeEmpty();
        frame.Should().NotBeNull("the live frames come out on standard output");
        frame!.Jpeg[..2].Should().Equal(0xFF, 0xD8);
        Directory.GetFiles(_root, "seg-*.mp4").Should().ContainSingle().Which.Should().Match(path => new FileInfo(path).Length > 10_000);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
