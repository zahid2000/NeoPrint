using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The camera recorder (T-482): what FFmpeg prints is read right, the command
/// line records one playable minute per file without sound, and the recorder
/// starts FFmpeg only when it may and says why when it cannot.
/// </summary>
public sealed class CameraRecorderTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string PublicKey = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo());

    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-recorder-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Start);
    private readonly FakeHost _host = new();
    private KioskCameraSettings? _settings = new(KioskCameraSettings.Activity, 2, 20, "1280x720", 10, "cam-test", PublicKey);

    private CameraRecorder Recorder() => new(
        new CameraVault(_root, _clock, NullLogger.Instance, _ => 100L * 1024 * 1024 * 1024, TimeZoneInfo.Utc),
        _host,
        () => _settings,
        () => false,
        _clock,
        NullLogger.Instance);

    // -----------------------------------------------------------------------
    // What FFmpeg prints
    // -----------------------------------------------------------------------

    [Fact]
    public void The_best_encoder_ffmpeg_has_is_picked()
    {
        const string Encoders = """
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D h264_mf              H264 via MediaFoundation (codec h264)
             V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
            """;

        FfmpegOutput.PickEncoder(Encoders).Should().Be("libopenh264");
        FfmpegOutput.PickEncoder(Encoders.Replace("libopenh264", "other", StringComparison.Ordinal)).Should().Be("h264_mf");
        FfmpegOutput.PickEncoder(" V....D libvpx  libvpx VP8").Should().BeNull("only the H.264 encoders are taken");
    }

    [Fact]
    public void The_cameras_directshow_lists_are_read_in_both_of_ffmpegs_ways()
    {
        const string Newer = """
            [dshow @ 000001] "Integrated Camera" (video)
            [dshow @ 000001]   Alternative name "@device_pnp_\\?\usb#vid_04f2"
            [dshow @ 000001] "Microphone Array (Realtek)" (audio)
            [dshow @ 000001] "Logitech C270" (video)
            """;
        const string Older = """
            [dshow @ 0000] DirectShow video devices (some may be both video and audio devices)
            [dshow @ 0000]  "USB2.0 HD UVC WebCam"
            [dshow @ 0000]     Alternative name "@device_pnp_\\?\usb"
            [dshow @ 0000] DirectShow audio devices
            [dshow @ 0000]  "Microphone"
            """;

        FfmpegOutput.DirectShowVideoDevices(Newer).Should().Equal("Integrated Camera", "Logitech C270");
        FfmpegOutput.DirectShowVideoDevices(Older).Should().Equal("USB2.0 HD UVC WebCam");
    }

    [Fact]
    public void On_linux_the_capture_node_of_each_camera_is_found()
    {
        var sys = Path.Combine(_root, "sys");
        void Node(string id, string index, string name)
        {
            Directory.CreateDirectory(Path.Combine(sys, id));
            File.WriteAllText(Path.Combine(sys, id, "index"), index + "\n");
            File.WriteAllText(Path.Combine(sys, id, "name"), name + "\n");
        }

        Node("video0", "0", "HD Webcam");
        Node("video1", "1", "HD Webcam");
        Node("video10", "0", "Second camera");

        V4l2Devices.Find(sys, "/dev").Select(device => device.Input).Should().Equal("/dev/video0", "/dev/video10");
        V4l2Devices.Find(Path.Combine(_root, "none")).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // The command line
    // -----------------------------------------------------------------------

    [Fact]
    public void The_command_records_a_playable_minute_per_file_without_sound()
    {
        var args = FfmpegCommand.Record(new CameraDevice("Cam", "dshow", "Logitech C270"), "libopenh264", _settings!, "out/seg-%Y%m%dT%H%M%S.mp4");

        args.Should().ContainInOrder("-f", "dshow", "-video_size", "1280x720", "-framerate", "10", "-i", "video=Logitech C270");
        args.Should().Contain("-an", "no sound is kept");
        args.Should().ContainInOrder("-c:v", "libopenh264", "-b:v", "1020k");
        args.Should().Contain(arg => arg.Contains("mpdecimate=max=50", StringComparison.Ordinal), "a still picture is not stored ten times a second");
        args.Should().ContainInOrder("-fps_mode", "vfr");
        args.Should().ContainInOrder("-segment_time", "60", "-segment_atclocktime", "1");
        args.Should().Contain("movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=2000000:flush_packets=1", "a minute cut short by a power cut still plays");
        args.Should().ContainInOrder("-strftime", "1", "out/seg-%Y%m%dT%H%M%S.mp4");
        args.Should().Contain("-nostdin");
    }

    [Fact]
    public void A_camera_that_cannot_give_the_size_is_scaled_instead()
    {
        var args = FfmpegCommand.Record(new CameraDevice("Cam", "v4l2", "/dev/video0"), "libx264", _settings!, "x.mp4", askSize: false);

        args.Should().NotContain("-video_size");
        args.Should().ContainInOrder("-f", "v4l2", "-i", "/dev/video0");
        args.Should().Contain(arg => arg.Contains("scale=1280:720", StringComparison.Ordinal));
        args.Should().ContainInOrder("-preset", "veryfast", "-tune", "zerolatency", "-crf", "28");
    }

    [Theory]
    [InlineData("Low", "32", "600k")]
    [InlineData("Medium", "28", "1020k")]
    [InlineData("High", "23", "1800k")]
    [InlineData("Ultra", "28", "1020k")]
    public void The_quality_the_panel_set_is_what_ffmpeg_is_asked_for(string quality, string crf, string ceiling)
    {
        var args = FfmpegCommand.Record(new CameraDevice("Cam", "v4l2", "/dev/video0"), "libx264", _settings! with { Quality = quality }, "x.mp4");

        args.Should().ContainInOrder("-crf", crf, "-maxrate", ceiling);
    }

    [Fact]
    public async Task Another_quality_starts_ffmpeg_again_with_it()
    {
        var recorder = Recorder();
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _settings = _settings! with { Quality = "Low" };
        _clock.Now = Start.AddSeconds(5);
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().HaveCount(2);
        _host.Started[1].Arguments.Should().Contain("600k");
    }

    [Theory]
    [InlineData(640, 10, 510)]
    [InlineData(1280, 10, 1020)]
    [InlineData(1920, 15, 3187)]
    [InlineData(640, 2, 200)]
    public void The_bitrate_follows_the_size_and_the_rate(int width, int fps, int kbit)
        => FfmpegCommand.Bitrate(width, fps).Should().Be(kbit);

    // -----------------------------------------------------------------------
    // The recorder
    // -----------------------------------------------------------------------

    [Fact]
    public async Task It_records_once_ffmpeg_an_encoder_and_a_camera_are_there()
    {
        var recorder = Recorder();
        var changes = new List<bool>();
        recorder.RecordingChanged += changes.Add;

        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().ContainSingle();
        recorder.Status.Recording.Should().BeFalse("not until FFmpeg has run past a bad start");

        _clock.Now = Start + CameraRecorder.QuickExit;
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        recorder.Status.Should().BeEquivalentTo(new { Enabled = true, Connected = true, Recording = true, Mode = "Activity", Error = (string?)null });
        changes.Should().Equal(true);
    }

    [Fact]
    public async Task Without_ffmpeg_it_says_so_and_starts_nothing()
    {
        _host.Ffmpeg = null;
        var recorder = Recorder();

        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().BeEmpty();
        recorder.Status.Error.Should().Be(CameraError.NoRecorder);
    }

    [Fact]
    public async Task Without_a_camera_it_says_so()
    {
        _host.Camera = null;
        var recorder = Recorder();

        await recorder.StepAsync(TestContext.Current.CancellationToken);

        recorder.Status.Should().BeEquivalentTo(new { Connected = (bool?)false, Recording = false, Error = CameraError.NoCamera });
    }

    [Fact]
    public async Task Without_a_key_nothing_is_recorded()
    {
        _settings = _settings! with { PublicKey = null };
        var recorder = Recorder();

        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().BeEmpty();
        recorder.Status.Error.Should().Be(CameraError.NoKey);
    }

    [Fact]
    public async Task Turned_off_it_stops_ffmpeg_and_the_screen_stops_saying_so()
    {
        var recorder = Recorder();
        await recorder.StepAsync(TestContext.Current.CancellationToken);
        _clock.Now = Start + CameraRecorder.QuickExit;
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _settings = null;
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Single().Stopped.Should().BeTrue();
        recorder.Status.Enabled.Should().BeFalse();
        recorder.Status.Recording.Should().BeFalse();
    }

    [Fact]
    public async Task A_camera_that_refuses_the_size_is_tried_again_without_it()
    {
        var recorder = Recorder();
        await recorder.StepAsync(TestContext.Current.CancellationToken);
        _host.Started[0].Exit(1);

        _clock.Now = Start.AddSeconds(5);
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().HaveCount(2);
        _host.Started[0].Arguments.Should().Contain("-video_size");
        _host.Started[1].Arguments.Should().NotContain("-video_size");
    }

    [Fact]
    public async Task A_recorder_that_keeps_falling_over_is_reported_and_tried_less_often()
    {
        var recorder = Recorder();

        // Every FFmpeg falls over at once; the recorder looks every five seconds.
        for (var look = 0; look < 40; look++)
        {
            await recorder.StepAsync(TestContext.Current.CancellationToken);
            foreach (var process in _host.Started.Where(process => !process.HasExited))
            {
                process.Exit(1);
            }

            _clock.Now += CameraRecorder.Poll;
        }

        recorder.Status.Error.Should().Be(CameraError.RecorderFailed);
        recorder.Status.Recording.Should().BeFalse();
        _host.Started.Should().HaveCount(4, "one try with the size, one without, then 15 s and a minute apart - and five minutes after the third failure");
    }

    [Fact]
    public async Task Another_size_starts_ffmpeg_again_with_it()
    {
        var recorder = Recorder();
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _settings = _settings! with { Resolution = "640x480" };
        _clock.Now = Start.AddSeconds(5);
        await recorder.StepAsync(TestContext.Current.CancellationToken);

        _host.Started.Should().HaveCount(2);
        _host.Started[0].Stopped.Should().BeTrue();
        _host.Started[1].Arguments.Should().Contain("640x480");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime Now { get; set; } = now;

        public DateTime UtcNow => Now;
    }

    private sealed class FakeHost : ICameraHost
    {
        public string? Ffmpeg { get; set; } = "ffmpeg";

        public CameraDevice? Camera { get; set; } = new("Test camera", "v4l2", "/dev/video0");

        public List<FakeProcess> Started { get; } = [];

        public string? FindFfmpeg() => Ffmpeg;

        public Task<string> AskAsync(string ffmpeg, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => Task.FromResult(" V....D libopenh264  OpenH264\n");

        public Task<CameraDevice?> FindCameraAsync(string ffmpeg, KioskCameraSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(Camera);

        public IRecorderProcess Start(string ffmpeg, IReadOnlyList<string> arguments)
        {
            var process = new FakeProcess(Started.Count + 100, arguments);
            Started.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(int id, IReadOnlyList<string> arguments) : IRecorderProcess
    {
        public int Id { get; } = id;

        public IReadOnlyList<string> Arguments { get; } = arguments;

        public bool HasExited { get; private set; }

        public int? ExitCode { get; private set; }

        public bool Stopped { get; private set; }

        public string ErrorTail => "Could not open video device";

        public void Exit(int code)
        {
            HasExited = true;
            ExitCode = code;
        }

        public void Stop()
        {
            Stopped = true;
            HasExited = true;
        }

        public void Dispose() => Stop();
    }
}
