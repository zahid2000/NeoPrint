using System.Net;
using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Watching the camera from the panel (T-483): FFmpeg gives small JPEGs
/// beside the recording; they leave the kiosk only while somebody watches;
/// the recorded minutes the panel asks for are sent, only those of the span.
/// </summary>
public sealed class CameraViewingTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string PublicKey = Convert.ToBase64String(RSA.Create(2048).ExportSubjectPublicKeyInfo());

    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-viewing-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Start);

    [Fact]
    public void Ffmpeg_also_gives_small_jpegs_twice_a_second_on_its_output()
    {
        var settings = new KioskCameraSettings(KioskCameraSettings.Activity, 2, 20, "1280x720", 10, "cam-test", PublicKey);
        var args = FfmpegCommand.Record(new CameraDevice("Cam", "dshow", "Logitech C270"), "libopenh264", settings, "out/seg-%Y%m%dT%H%M%S.mp4");

        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        graph.Should().Contain("split=2").And.Contain("fps=2,scale=640:-2").And.Contain("mpdecimate=max=50");

        // The recording first, as before; then the frames, on standard output.
        args.Should().ContainInOrder("-map", "[rec]", "-c:v", "libopenh264", "out/seg-%Y%m%dT%H%M%S.mp4", "-map", "[live]", "-c:v", "mjpeg");
        args.Should().EndWith(["-f", "image2pipe", "pipe:1"]);
    }

    [Fact]
    public void The_jpegs_are_cut_one_by_one_however_the_bytes_arrive()
    {
        byte[] first = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0xFF, 0x00, 0x20, 0xFF, 0xD9];
        byte[] second = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];
        byte[] stream = [0x00, 0x13, .. first, .. second, 0xFF, 0xD8, 0x05];

        var splitter = new JpegFrameSplitter();
        var frames = new List<byte[]>();
        foreach (var chunk in stream.Chunk(3))
        {
            frames.AddRange(splitter.Push(chunk));
        }

        frames.Should().HaveCount(2);
        frames[0].Should().Equal(first);
        frames[1].Should().Equal(second);

        // The third, unfinished, comes with the next bytes.
        splitter.Push([0x06, 0xFF, 0xD9]).Should().ContainSingle().Which.Should().Equal([0xFF, 0xD8, 0x05, 0x06, 0xFF, 0xD9]);
    }

    [Fact]
    public async Task Live_frames_go_while_the_backend_says_somebody_watches_each_once()
    {
        var frame = new CameraFrame([0xFF, 0xD8, 0xFF, 0xD9], Start);
        var sent = 0;
        var sender = new CameraLiveSender(
            () => frame,
            (_, _) =>
            {
                sent++;
                return Task.FromResult(sent < 3);
            },
            _clock,
            NullLogger.Instance,
            (wait, _) =>
            {
                // Half a second on: a new frame each time.
                _clock.Now += wait;
                frame = frame with { At = _clock.Now };
                return Task.CompletedTask;
            });

        await sender.RunAsync(TestContext.Current.CancellationToken);

        sent.Should().Be(3, "the third answer said nobody watches any more");
        sender.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Without_frames_the_live_view_stops_on_its_own_and_sends_nothing_old()
    {
        var sent = 0;
        var stale = new CameraFrame([0xFF, 0xD8, 0xFF, 0xD9], Start.AddMinutes(-1));
        var sender = new CameraLiveSender(
            () => stale,
            (_, _) =>
            {
                sent++;
                return Task.FromResult(true);
            },
            _clock,
            NullLogger.Instance,
            (wait, _) =>
            {
                _clock.Now += wait;
                return Task.CompletedTask;
            });

        await sender.RunAsync(TestContext.Current.CancellationToken);

        sent.Should().Be(0);
        _clock.Now.Should().BeAfter(Start + CameraLiveSender.GiveUpWithoutFrames);
    }

    [Fact]
    public async Task The_minutes_asked_for_are_sent_only_those_of_the_span_and_the_request_answered()
    {
        var vault = Vault();
        Sealed(vault, Start.AddMinutes(-10));
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        Sealed(vault, Start.AddMinutes(30));

        var request = new CameraFootageRequest(Guid.NewGuid(), Start, Start.AddMinutes(5));
        var sent = new List<DateTime>();
        var answered = new Dictionary<Guid, int>();
        var footage = new CameraFootageSender(
            vault,
            _ => Task.FromResult<IReadOnlyList<CameraFootageRequest>>(answered.ContainsKey(request.Id) ? [] : [request]),
            (id, minute, _) =>
            {
                id.Should().Be(request.Id);
                sent.Add(minute.Start);
                return Task.CompletedTask;
            },
            (id, minutes, _) =>
            {
                answered[id] = minutes;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        (await footage.StepAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        sent.Should().Equal(Start, Start.AddMinutes(1));
        answered[request.Id].Should().Be(2);

        // Answered: not sent again.
        (await footage.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task A_backend_out_of_reach_leaves_the_request_open_for_the_next_round()
    {
        var vault = Vault();
        Sealed(vault, Start);
        var request = new CameraFootageRequest(Guid.NewGuid(), Start, Start.AddMinutes(5));
        var completed = false;
        var reachable = false;
        var footage = new CameraFootageSender(
            vault,
            _ => Task.FromResult<IReadOnlyList<CameraFootageRequest>>(completed ? [] : [request]),
            (_, _, _) => reachable ? Task.CompletedTask : throw new HttpRequestException("offline"),
            (_, _, _) =>
            {
                completed = true;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        (await footage.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        completed.Should().BeFalse();

        reachable = true;
        (await footage.StepAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        completed.Should().BeTrue();
    }

    [Fact]
    public async Task A_minute_the_backend_refuses_does_not_stop_the_others()
    {
        var vault = Vault();
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        var request = new CameraFootageRequest(Guid.NewGuid(), Start, Start.AddMinutes(5));
        var answered = -1;
        var footage = new CameraFootageSender(
            vault,
            _ => Task.FromResult<IReadOnlyList<CameraFootageRequest>>(answered >= 0 ? [] : [request]),
            (_, minute, _) => minute.Start == Start
                ? throw new KioskApiException(HttpStatusCode.Conflict, "camera.segmentInvalid", "refused")
                : Task.CompletedTask,
            (_, minutes, _) =>
            {
                answered = minutes;
                return Task.CompletedTask;
            },
            NullLogger.Instance);

        await footage.StepAsync(TestContext.Current.CancellationToken);

        answered.Should().Be(1);
    }

    private CameraVault Vault() => new(_root, _clock, NullLogger.Instance, _ => 100L * 1024 * 1024 * 1024, TimeZoneInfo.Utc);

    private static void Sealed(CameraVault vault, DateTime start)
    {
        vault.Prepare();
        File.WriteAllBytes(Path.Combine(vault.SealedDirectory, CameraVault.SealedName(start, TimeSpan.FromSeconds(60))), [1, 2, 3]);
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
}
