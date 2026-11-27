using System.Net;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The camera's minutes to the file server (T-485): oldest first, each once;
/// the kiosk keeps its own copy; a backend out of reach is tried again later,
/// one that will never take a minute does not stop the others.
/// </summary>
public sealed class CameraArchiveUploaderTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-archive-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Start);
    private readonly List<DateTime> _sent = [];
    private KioskCameraSettings? _settings = new(Archive: true);
    private Func<SealedSegment, Exception?> _answer = _ => null;

    private CameraVault Vault() => new(_root, _clock, NullLogger.Instance, _ => 100L * 1024 * 1024 * 1024, TimeZoneInfo.Utc);

    private CameraArchiveUploader Uploader(CameraVault vault) => new(
        vault,
        () => _settings,
        (segment, _) =>
        {
            if (_answer(segment) is { } failure)
            {
                throw failure;
            }

            _sent.Add(segment.Start);
            return Task.CompletedTask;
        },
        _clock,
        NullLogger.Instance);

    private string Sealed(CameraVault vault, DateTime start)
    {
        vault.Prepare();
        var path = Path.Combine(vault.SealedDirectory, CameraVault.SealedName(start, TimeSpan.FromSeconds(60)));
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task The_minutes_go_oldest_first_each_once_and_stay_on_the_kiosk()
    {
        var vault = Vault();
        Sealed(vault, Start.AddMinutes(2));
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        var uploader = Uploader(vault);

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0, "nothing is sent twice");

        _sent.Should().Equal(Start, Start.AddMinutes(1), Start.AddMinutes(2));
        uploader.Unsent.Should().Be(0);
        uploader.LastSentAt.Should().Be(Start);
        vault.Storage.Segments.Should().Be(3, "the kiosk keeps its own copy until its own time");
        Directory.GetFiles(vault.SentDirectory).Should().HaveCount(3);
    }

    [Fact]
    public async Task Without_a_file_server_nothing_is_sent_and_nothing_is_counted()
    {
        var vault = Vault();
        Sealed(vault, Start);
        _settings = new KioskCameraSettings(Archive: false);
        var uploader = Uploader(vault);

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        _sent.Should().BeEmpty();
        uploader.Unsent.Should().BeNull();
        vault.Unsent().Should().ContainSingle("it waits, should a file server be chosen later");
    }

    [Fact]
    public async Task A_backend_out_of_reach_is_tried_again_a_minute_later()
    {
        var vault = Vault();
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        var uploader = Uploader(vault);
        _answer = _ => new HttpRequestException("no route");

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        uploader.Unsent.Should().Be(2);

        _answer = _ => null;
        _clock.Now = Start.AddSeconds(30);
        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0, "not before a minute has passed");

        _clock.Now = Start + CameraArchiveUploader.AfterFailure;
        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task A_minute_the_backend_will_never_take_does_not_stop_the_others()
    {
        var vault = Vault();
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        var uploader = Uploader(vault);
        _answer = segment => segment.Start == Start
            ? new KioskApiException(HttpStatusCode.Conflict, "camera.segmentInvalid", "refused")
            : null;

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        _sent.Should().Equal(Start.AddMinutes(1));
        vault.Unsent().Should().BeEmpty("the refused one is not sent again and again");
    }

    [Fact]
    public async Task Turned_off_on_the_server_it_stops_until_the_configuration_says_so()
    {
        var vault = Vault();
        Sealed(vault, Start);
        var uploader = Uploader(vault);
        _answer = _ => new KioskApiException(HttpStatusCode.Conflict, "camera.archiveOff", "off");

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(0);

        vault.Unsent().Should().ContainSingle("it stays, to go when a file server is chosen again");
    }

    [Fact]
    public async Task A_long_backlog_goes_a_round_at_a_time()
    {
        var vault = Vault();
        for (var minute = 0; minute < CameraArchiveUploader.PerRound + 5; minute++)
        {
            Sealed(vault, Start.AddMinutes(minute));
        }

        var uploader = Uploader(vault);

        (await uploader.StepAsync(TestContext.Current.CancellationToken)).Should().Be(CameraArchiveUploader.PerRound);
        uploader.Unsent.Should().Be(5);
    }

    [Fact]
    public void A_minute_already_sent_is_still_pruned_with_the_rest()
    {
        var vault = Vault();
        var segment = CameraVault.ParseSealed(Sealed(vault, Start.AddDays(-3)), 3)!;
        vault.MarkSent(segment);

        vault.Prune(new KioskCameraSettings(RetentionDays: 2));

        Directory.GetFiles(vault.SentDirectory).Should().BeEmpty();
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
