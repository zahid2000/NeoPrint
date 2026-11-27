using System.Security.Cryptography;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Crypto;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Camera;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The camera's recordings on the kiosk's disk (T-482): each finished minute
/// sealed for the backend and its plain file gone; Activity mode keeps only
/// the minutes somebody was at the kiosk; two days by default, within the
/// disk limit, never at the print spool's cost.
/// </summary>
public sealed class CameraVaultTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly RSA Backend = RSA.Create(2048);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "neoprint-camera-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(Start);
    private long? _free = 100L * 1024 * 1024 * 1024;

    private CameraVault Vault() => new(_root, _clock, NullLogger.Instance, _ => _free, TimeZoneInfo.Utc);

    private static KioskCameraSettings Settings(string mode = KioskCameraSettings.Continuous, int days = 2, int maxGb = 20)
        => new(mode, days, maxGb, "1280x720", 10, "cam-test", Convert.ToBase64String(Backend.ExportSubjectPublicKeyInfo()));

    /// <summary>A minute FFmpeg wrote, named as it names them: the local time it started.</summary>
    private string Written(CameraVault vault, DateTime startUtc, int bytes = 4096)
    {
        vault.Prepare();
        var path = Path.Combine(vault.WritingDirectory, $"seg-{startUtc:yyyyMMdd'T'HHmmss}.mp4");
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(bytes));
        File.SetLastWriteTimeUtc(path, startUtc.AddSeconds(60));
        return path;
    }

    private string Sealed(CameraVault vault, DateTime startUtc, long bytes = 1024)
    {
        vault.Prepare();
        var path = Path.Combine(vault.SealedDirectory, CameraVault.SealedName(startUtc, TimeSpan.FromSeconds(60)));

        // Only the length counts; nothing is written (sparse where the file system allows).
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(bytes);
        return path;
    }

    [Fact]
    public async Task A_finished_minute_is_sealed_for_the_backend_and_its_plain_file_is_gone()
    {
        var vault = Vault();
        var first = Written(vault, Start);
        var plain = await File.ReadAllBytesAsync(first, TestContext.Current.CancellationToken);
        var writing = Written(vault, Start.AddMinutes(1));
        _clock.Now = Start.AddMinutes(1).AddSeconds(5);

        var count = await vault.SealAsync(Settings(), new ActivityLog(), writing: true, TestContext.Current.CancellationToken);

        count.Should().Be(1);
        File.Exists(first).Should().BeFalse("no plain video stays once it is sealed");
        File.Exists(writing).Should().BeTrue("the minute FFmpeg is writing is left alone");

        var sealedFile = Directory.GetFiles(vault.SealedDirectory).Should().ContainSingle().Subject;
        Path.GetFileName(sealedFile).Should().Be("20261127T100000Z-60.npcam");

        await using var input = File.OpenRead(sealedFile);
        using var opened = new MemoryStream();
        await CameraSegmentCipher.DecryptAsync(input, opened, id => id == "cam-test" ? Backend : null, TestContext.Current.CancellationToken);
        opened.ToArray().Should().Equal(plain, "only the backend's private key opens it");
    }

    [Fact]
    public async Task When_the_recorder_has_stopped_its_last_minute_is_sealed_too()
    {
        var vault = Vault();
        Written(vault, Start);
        _clock.Now = Start.AddMinutes(2);

        (await vault.SealAsync(Settings(), new ActivityLog(), writing: false, TestContext.Current.CancellationToken)).Should().Be(1);
        Directory.GetFiles(vault.WritingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task In_activity_mode_a_minute_nobody_came_near_is_deleted_unsealed()
    {
        var vault = Vault();
        var quiet = Written(vault, Start);
        Written(vault, Start.AddMinutes(1));

        // Still within the minute after it: somebody may yet come.
        _clock.Now = Start.AddMinutes(1).AddSeconds(30);
        (await vault.SealAsync(Settings(KioskCameraSettings.Activity), new ActivityLog(), true, TestContext.Current.CancellationToken)).Should().Be(0);
        File.Exists(quiet).Should().BeTrue();

        _clock.Now = Start.AddMinutes(2).AddSeconds(1);
        (await vault.SealAsync(Settings(KioskCameraSettings.Activity), new ActivityLog(), true, TestContext.Current.CancellationToken)).Should().Be(0);

        File.Exists(quiet).Should().BeFalse();
        Directory.GetFiles(vault.SealedDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task In_activity_mode_the_minutes_around_a_customer_are_kept()
    {
        var vault = Vault();
        var activity = new ActivityLog();

        // Somebody at the kiosk from 10:02:30 to 10:03:10.
        activity.Mark(true, Start.AddMinutes(2).AddSeconds(30));
        activity.Mark(false, Start.AddMinutes(3).AddSeconds(10));

        for (var minute = 0; minute < 6; minute++)
        {
            Written(vault, Start.AddMinutes(minute));
        }

        _clock.Now = Start.AddMinutes(10);
        await vault.SealAsync(Settings(KioskCameraSettings.Activity), activity, writing: false, TestContext.Current.CancellationToken);

        Directory.GetFiles(vault.SealedDirectory).Select(Path.GetFileName).Should().BeEquivalentTo(
            ["20261127T100100Z-60.npcam", "20261127T100200Z-60.npcam", "20261127T100300Z-60.npcam", "20261127T100400Z-60.npcam"],
            "a minute either side of the visit is kept, nothing further");
        Directory.GetFiles(vault.WritingDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_key_nothing_stays_on_the_disk_in_the_clear()
    {
        var vault = Vault();
        Written(vault, Start);
        _clock.Now = Start.AddMinutes(3);

        await vault.SealAsync(Settings() with { PublicKey = null }, new ActivityLog(), writing: false, TestContext.Current.CancellationToken);

        Directory.GetFiles(vault.WritingDirectory).Should().BeEmpty();
        Directory.GetFiles(vault.SealedDirectory).Should().BeEmpty();
        CameraVault.CanSeal(Settings() with { PublicKey = "not a key" }).Should().BeFalse();
        CameraVault.CanSeal(Settings()).Should().BeTrue();
    }

    [Fact]
    public void Recordings_older_than_the_retention_are_deleted()
    {
        var vault = Vault();
        var old = Sealed(vault, Start.AddDays(-2).AddMinutes(-2));
        var kept = Sealed(vault, Start.AddDays(-1));

        var storage = vault.Prune(Settings(days: 2));

        File.Exists(old).Should().BeFalse();
        File.Exists(kept).Should().BeTrue();
        storage.Segments.Should().Be(1);
        storage.OldestAt.Should().Be(Start.AddDays(-1));
    }

    [Fact]
    public void The_retention_is_never_past_a_month_whatever_the_configuration_says()
    {
        var vault = Vault();
        var old = Sealed(vault, Start.AddDays(-32));

        vault.Prune(Settings(days: 400));

        File.Exists(old).Should().BeFalse();
    }

    [Fact]
    public void Over_the_disk_limit_the_oldest_go_first()
    {
        var vault = Vault();
        const long Mb = 1024 * 1024;
        var oldest = Sealed(vault, Start.AddHours(-3), 600 * Mb);
        var middle = Sealed(vault, Start.AddHours(-2), 300 * Mb);
        var newest = Sealed(vault, Start.AddHours(-1), 300 * Mb);

        // A gigabyte allowed; 1.2 GB kept.
        vault.Prune(Settings(maxGb: 1));

        File.Exists(oldest).Should().BeFalse();
        File.Exists(middle).Should().BeTrue();
        File.Exists(newest).Should().BeTrue();
    }

    [Fact]
    public void The_spool_keeps_its_room_recordings_make_way()
    {
        var vault = Vault();
        var oldest = Sealed(vault, Start.AddHours(-2));
        Sealed(vault, Start.AddHours(-1));
        _free = CameraVault.ReserveBytes - 1;

        var storage = vault.Prune(Settings());

        File.Exists(oldest).Should().BeFalse();
        storage.Segments.Should().Be(0, "the free space does not come back here, so all go");
        vault.HasRoom.Should().BeFalse("and there is no room to record");
    }

    [Fact]
    public void Turned_off_the_last_retention_still_applies()
    {
        var vault = Vault();
        vault.Prune(Settings(days: 7));
        var fiveDays = Sealed(vault, Start.AddDays(-5));
        var tenDays = Sealed(vault, Start.AddDays(-10));

        vault.Prune(null);

        File.Exists(fiveDays).Should().BeTrue("seven days were set when it was on");
        File.Exists(tenDays).Should().BeFalse();
    }

    [Fact]
    public void The_minutes_of_a_stretch_of_time_are_found_by_their_names()
    {
        var vault = Vault();
        Sealed(vault, Start);
        Sealed(vault, Start.AddMinutes(1));
        Sealed(vault, Start.AddMinutes(2));

        vault.Between(Start.AddSeconds(90), Start.AddSeconds(150)).Select(segment => segment.Start)
            .Should().Equal(Start.AddMinutes(1), Start.AddMinutes(2));
    }

    [Fact]
    public void A_written_name_is_read_in_the_machines_own_time()
    {
        var baku = TimeZoneInfo.CreateCustomTimeZone("Baku", TimeSpan.FromHours(4), "Baku", "Baku");
        var vault = new CameraVault(_root, _clock, NullLogger.Instance, _ => _free, baku);

        vault.WrittenStart(Path.Combine("x", "seg-20261127T140000.mp4")).Should().Be(Start);
        vault.WrittenStart(Path.Combine("x", "live.jpg")).Should().BeNull();
    }

    [Fact]
    public void Somebody_still_at_the_kiosk_counts_as_there_until_they_leave()
    {
        var activity = new ActivityLog();
        activity.Mark(true, Start);

        activity.Overlaps(Start.AddMinutes(5), Start.AddMinutes(6)).Should().BeTrue();
        activity.Overlaps(Start.AddMinutes(-6), Start.AddMinutes(-5)).Should().BeFalse();
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
