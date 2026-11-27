using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;

using NeoPrint.Crypto;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>
/// The camera's recordings on the kiosk's disk (T-482): FFmpeg writes one
/// minute at a time into <see cref="WritingDirectory"/>; each finished minute
/// is sealed for the backend (<see cref="CameraSegmentCipher"/>) into
/// <see cref="SealedDirectory"/> and the plain file deleted. Sealed minutes
/// are kept for <c>Camera.RetentionDays</c>, within <c>Camera.MaxDiskGb</c>,
/// and never at the cost of the disk the print spool needs.
/// </summary>
/// <remarks>
/// <para>
/// A kiosk is an untrusted device (docs/SECURITY_THREAT_MODEL.md): the kiosk
/// holds only the backend's public key, so what it sealed it cannot read back.
/// The one plain minute FFmpeg is writing - and, in <c>Activity</c> mode, the
/// minute after it that decides whether it is kept - is all a stolen kiosk
/// could show.
/// </para>
/// <para>
/// In <c>Activity</c> mode a minute is kept only when somebody was at the
/// kiosk within <see cref="ActivityMargin"/> of it; the others are deleted
/// unsealed, once the margin after them has passed.
/// </para>
/// </remarks>
public sealed class CameraVault
{
    /// <summary>How long before and after somebody at the kiosk is kept in <c>Activity</c> mode.</summary>
    public static readonly TimeSpan ActivityMargin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The free disk the camera always leaves: more than telemetry's 2 GB
    /// "disk low" limit, so recordings never push the kiosk into it and the
    /// spool always has room.
    /// </summary>
    public const long ReserveBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>The retention used when the camera is turned off and nothing better is known.</summary>
    public const int DefaultDays = 2;

    public const string SealedExtension = ".npcam";

    private const string WritingPrefix = "seg-";
    private const string WritingFormat = "yyyyMMdd'T'HHmmss";
    private const string SealedFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string RetentionFile = "retention-days";

    private readonly IClock _clock;
    private readonly Func<string, long?> _freeBytes;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger _logger;

    /// <param name="root">The camera's own folder, next to the spool.</param>
    /// <param name="freeBytes">The free space on the volume a path is on; null when it cannot be read.</param>
    /// <param name="zone">The zone FFmpeg names its files in: the machine's own.</param>
    public CameraVault(string root, IClock clock, ILogger logger, Func<string, long?>? freeBytes = null, TimeZoneInfo? zone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = root;
        _clock = clock;
        _logger = logger;
        _freeBytes = freeBytes ?? FreeBytesOf;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public string Root { get; }

    /// <summary>Where FFmpeg writes: plain video, one minute per file.</summary>
    public string WritingDirectory => Path.Combine(Root, "writing");

    /// <summary>The sealed minutes.</summary>
    public string SealedDirectory => Path.Combine(Root, "sealed");

    /// <summary>The sealed minutes already on the file server (T-485): kept here too, until their own time.</summary>
    public string SentDirectory => Path.Combine(SealedDirectory, "sent");

    /// <summary>The file name pattern FFmpeg is given (<c>-strftime 1</c>, the machine's local time).</summary>
    public string WritingPattern => Path.Combine(WritingDirectory, WritingPrefix + "%Y%m%dT%H%M%S.mp4");

    public void Prepare()
    {
        Directory.CreateDirectory(WritingDirectory);
        Directory.CreateDirectory(SealedDirectory);
        Directory.CreateDirectory(SentDirectory);
    }

    /// <summary>
    /// Seals the finished minutes, or deletes the ones <c>Activity</c> mode
    /// does not keep.
    /// </summary>
    /// <param name="writing">FFmpeg is running: its newest file is still being written and is left alone.</param>
    /// <returns>How many minutes were sealed.</returns>
    public async Task<int> SealAsync(
        KioskCameraSettings settings, ActivityLog activity, bool writing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(activity);

        if (!Directory.Exists(WritingDirectory))
        {
            return 0;
        }

        using var publicKey = PublicKeyOf(settings);
        var now = _clock.UtcNow;
        var segments = WrittenSegments();
        var sealedCount = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var (path, start) = segments[i];
            var last = i == segments.Count - 1;

            if (last && writing)
            {
                break;
            }

            // A file FFmpeg did not name, or one it gave up on at once.
            if (start is null || new FileInfo(path).Length == 0)
            {
                Delete(path);
                continue;
            }

            var end = last ? File.GetLastWriteTimeUtc(path) : segments[i + 1].Start ?? File.GetLastWriteTimeUtc(path);
            if (end <= start.Value)
            {
                end = start.Value.AddSeconds(1);
            }

            if (!settings.RecordsAlways)
            {
                // Whether anybody came in the minute after is not known yet.
                if (now < end + ActivityMargin)
                {
                    continue;
                }

                if (!activity.Overlaps(start.Value - ActivityMargin, end + ActivityMargin))
                {
                    Delete(path);
                    continue;
                }
            }

            // No key: nothing may stay on the disk in the clear.
            if (publicKey is null || settings.KeyId is null)
            {
                Delete(path);
                continue;
            }

            await SealOneAsync(path, start.Value, end, settings.KeyId, publicKey, cancellationToken).ConfigureAwait(false);
            sealedCount++;
        }

        return sealedCount;
    }

    /// <summary>
    /// Deletes what is past its time, then the oldest while the recordings
    /// take more than their limit or the disk is short of <see cref="ReserveBytes"/>.
    /// </summary>
    /// <param name="settings">Null while the camera is turned off: the last retention it had still applies.</param>
    public CameraStorage Prune(KioskCameraSettings? settings)
    {
        var days = settings?.Days ?? LastRetentionDays();
        if (settings is not null)
        {
            RememberRetention(settings.Days);
        }

        var now = _clock.UtcNow;
        var segments = SealedSegments();

        foreach (var segment in segments.Where(segment => segment.End < now.AddDays(-days)).ToList())
        {
            Delete(segment.Path);
            segments.Remove(segment);
        }

        var limit = settings?.MaxBytes ?? long.MaxValue;
        while (segments.Count > 0 && (segments.Sum(segment => segment.Bytes) > limit || _freeBytes(Root) < ReserveBytes))
        {
            Delete(segments[0].Path);
            segments.RemoveAt(0);
        }

        return StorageOf(segments);
    }

    /// <summary>Whether there is room to record: the reserve left free after pruning.</summary>
    public bool HasRoom => !(_freeBytes(Root) < ReserveBytes);

    /// <summary>What is kept now.</summary>
    public CameraStorage Storage => StorageOf(SealedSegments());

    /// <summary>Deletes every plain file: the camera was turned off, or there is no key to seal with.</summary>
    public void DiscardWriting()
    {
        if (!Directory.Exists(WritingDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(WritingDirectory))
        {
            Delete(file);
        }
    }

    /// <summary>The sealed minutes that overlap a stretch of time, oldest first (for M2's "send me this").</summary>
    public IReadOnlyList<SealedSegment> Between(DateTime fromUtc, DateTime toUtc)
        => [.. SealedSegments().Where(segment => segment.Start < toUtc && segment.End > fromUtc)];

    /// <summary>The sealed minutes not yet on the file server, oldest first (T-485).</summary>
    public IReadOnlyList<SealedSegment> Unsent()
    {
        if (!Directory.Exists(SealedDirectory))
        {
            return [];
        }

        return [.. new DirectoryInfo(SealedDirectory).EnumerateFiles("*" + SealedExtension, SearchOption.TopDirectoryOnly)
            .Select(file => ParseSealed(file.FullName, file.Length))
            .OfType<SealedSegment>()
            .OrderBy(segment => segment.Start)];
    }

    /// <summary>The file server has it: it moves aside, and stays here until its own time.</summary>
    public void MarkSent(SealedSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        Directory.CreateDirectory(SentDirectory);
        try
        {
            File.Move(segment.Path, Path.Combine(SentDirectory, Path.GetFileName(segment.Path)), overwrite: true);
        }
        catch (FileNotFoundException)
        {
            // Pruned in the meantime.
        }
    }

    /// <summary>A sealed segment's file name says when it starts and how long it is: <c>20261127T101500Z-60.npcam</c>.</summary>
    public static string SealedName(DateTime startUtc, TimeSpan length)
        => startUtc.ToString(SealedFormat, CultureInfo.InvariantCulture) + "-"
            + ((int)Math.Ceiling(length.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + SealedExtension;

    public static SealedSegment? ParseSealed(string path, long bytes)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(SealedExtension, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = name[..^SealedExtension.Length].Split('-');
        if (parts.Length != 2
            || !DateTime.TryParseExact(parts[0], SealedFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        return new SealedSegment(path, start, start.AddSeconds(seconds), bytes);
    }

    /// <summary>When a written file starts, from the name FFmpeg gave it in the machine's local time.</summary>
    public DateTime? WrittenStart(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(WritingPrefix, StringComparison.Ordinal)
            || !DateTime.TryParseExact(name[WritingPrefix.Length..], WritingFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
        }
        catch (ArgumentException)
        {
            // A time the clock skipped: no such minute.
            return null;
        }
    }

    private List<(string Path, DateTime? Start)> WrittenSegments()
        => [.. Directory.EnumerateFiles(WritingDirectory)
            .Where(path => !path.EndsWith(".tmp", StringComparison.Ordinal))
            .Select(path => (path, WrittenStart(path)))
            .OrderBy(segment => segment.Item2 ?? DateTime.MinValue)
            .ThenBy(segment => segment.path, StringComparer.Ordinal)];

    private List<SealedSegment> SealedSegments()
    {
        if (!Directory.Exists(SealedDirectory))
        {
            return [];
        }

        var segments = new List<SealedSegment>();

        foreach (var file in new DirectoryInfo(SealedDirectory).EnumerateFiles("*" + SealedExtension, SearchOption.AllDirectories))
        {
            if (ParseSealed(file.FullName, file.Length) is { } segment)
            {
                segments.Add(segment);
            }
            else
            {
                Delete(file.FullName);
            }
        }

        return [.. segments.OrderBy(segment => segment.Start)];
    }

    private async Task SealOneAsync(string path, DateTime start, DateTime end, string keyId, RSA publicKey, CancellationToken cancellationToken)
    {
        var target = Path.Combine(SealedDirectory, SealedName(start, end - start));
        var temporary = target + ".tmp";

        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await CameraSegmentCipher.EncryptAsync(input, output, keyId, publicKey, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
            Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Delete(temporary);
            _logger.LogWarning(exception, "A camera segment could not be sealed; it is tried again.");
        }
    }

    private static RSA? PublicKeyOf(KioskCameraSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PublicKey) || !CameraSegmentCipher.IsKeyId(settings.KeyId))
        {
            return null;
        }

        try
        {
            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(settings.PublicKey), out _);
            return rsa;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whether a configuration carries a key the kiosk can seal with.</summary>
    public static bool CanSeal(KioskCameraSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var key = PublicKeyOf(settings);
        return key is not null;
    }

    private static CameraStorage StorageOf(IReadOnlyList<SealedSegment> segments)
        => new(
            segments.Sum(segment => segment.Bytes),
            segments.Count,
            segments.Count > 0 ? segments[0].Start : null,
            segments.Count > 0 ? segments[^1].End : null);

    private int LastRetentionDays()
    {
        try
        {
            var path = Path.Combine(Root, RetentionFile);
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                ? Math.Clamp(days, 1, 31)
                : DefaultDays;
        }
        catch (IOException)
        {
            return DefaultDays;
        }
    }

    private void RememberRetention(int days)
    {
        if (LastRetentionDays() == days && File.Exists(Path.Combine(Root, RetentionFile)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, RetentionFile), days.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "The camera's retention could not be noted.");
        }
    }

    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // FFmpeg may still hold it; the next round tries again.
            _logger.LogDebug(exception, "A camera file could not be deleted yet.");
        }
    }

    private static long? FreeBytesOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One sealed minute on the disk.</summary>
public sealed record SealedSegment(string Path, DateTime Start, DateTime End, long Bytes);

/// <summary>What the camera keeps on the disk, as telemetry reports it.</summary>
public sealed record CameraStorage(long Bytes, int Segments, DateTime? OldestAt, DateTime? NewestAt);

/// <summary>
/// When somebody was at the kiosk (T-482): the stretches <c>Activity</c> mode
/// keeps, with <see cref="CameraVault.ActivityMargin"/> either side. Only the
/// last hour is remembered - a minute is decided within two.
/// </summary>
public sealed class ActivityLog
{
    private static readonly TimeSpan Memory = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly List<(DateTime From, DateTime To)> _spans = [];
    private DateTime? _since;

    /// <summary>Says whether somebody is at the kiosk now; called every few seconds.</summary>
    public void Mark(bool busy, DateTime now)
    {
        lock (_gate)
        {
            if (busy)
            {
                _since ??= now;
            }
            else if (_since is { } since)
            {
                _spans.Add((since, now));
                _since = null;
            }

            _spans.RemoveAll(span => span.To < now - Memory);
        }
    }

    /// <summary>Whether anybody was at the kiosk at any moment between the two times.</summary>
    public bool Overlaps(DateTime from, DateTime to)
    {
        lock (_gate)
        {
            return (_since is { } since && since <= to)
                || _spans.Any(span => span.From <= to && span.To >= from);
        }
    }
}
