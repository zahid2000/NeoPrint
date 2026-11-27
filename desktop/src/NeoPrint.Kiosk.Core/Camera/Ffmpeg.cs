using System.Globalization;
using System.Text.RegularExpressions;

namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>A camera FFmpeg can read from.</summary>
/// <param name="Name">What the panel and the log call it.</param>
/// <param name="Format">FFmpeg's input format: <c>dshow</c>, <c>v4l2</c>, or <c>lavfi</c> for the fake camera.</param>
/// <param name="Input">What follows <c>-i</c>.</param>
public sealed record CameraDevice(string Name, string Format, string Input)
{
    /// <summary>
    /// A moving test picture instead of a camera, for a developer's machine
    /// (<c>Kiosk:FakeCamera</c>): the whole chain runs without one.
    /// </summary>
    public static CameraDevice Fake(int width, int height, int fps)
        => new("Test pattern", "lavfi", string.Create(CultureInfo.InvariantCulture, $"testsrc2=size={width}x{height}:rate={fps}"));
}

/// <summary>
/// What FFmpeg prints, read back (T-482): plain parsing, testable on any
/// platform.
/// </summary>
public static partial class FfmpegOutput
{
    /// <summary>
    /// The H.264 encoders the kiosk may use, best first: OpenH264 (BSD, in
    /// the Windows build provisioning installs), Windows' own Media
    /// Foundation encoder, and the one in the distribution's FFmpeg on Linux.
    /// </summary>
    public static readonly IReadOnlyList<string> Encoders = ["libopenh264", "h264_mf", "libx264"];

    /// <summary>The first encoder of <see cref="Encoders"/> that <c>ffmpeg -encoders</c> lists.</summary>
    public static string? PickEncoder(string encodersOutput)
    {
        ArgumentNullException.ThrowIfNull(encodersOutput);

        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in encodersOutput.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // " V....D libopenh264   OpenH264 H.264 ..." - the flags, then the name.
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] == 'V')
            {
                listed.Add(parts[1]);
            }
        }

        return Encoders.FirstOrDefault(listed.Contains);
    }

    /// <summary>
    /// The video devices <c>ffmpeg -list_devices true -f dshow -i dummy</c>
    /// names, in its order. Both of FFmpeg's ways of printing them are read:
    /// <c>"Name" (video)</c>, and a "DirectShow video devices" heading.
    /// </summary>
    public static IReadOnlyList<string> DirectShowVideoDevices(string listOutput)
    {
        ArgumentNullException.ThrowIfNull(listOutput);

        var devices = new List<string>();
        var inVideoSection = false;

        foreach (var raw in listOutput.Split('\n'))
        {
            var line = DShowPrefix().Replace(raw.TrimEnd('\r'), string.Empty).Trim();

            if (line.StartsWith("DirectShow video devices", StringComparison.OrdinalIgnoreCase))
            {
                inVideoSection = true;
                continue;
            }

            if (line.StartsWith("DirectShow audio devices", StringComparison.OrdinalIgnoreCase))
            {
                inVideoSection = false;
                continue;
            }

            if (line.StartsWith("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = QuotedName().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value;
            if (kind.Contains("video", StringComparison.OrdinalIgnoreCase) || (kind.Length == 0 && inVideoSection))
            {
                devices.Add(match.Groups["name"].Value);
            }
        }

        return devices;
    }

    [GeneratedRegex(@"^\[[^\]]*\]\s*")]
    private static partial Regex DShowPrefix();

    [GeneratedRegex("^\"(?<name>[^\"]+)\"\\s*(\\((?<kind>[^)]*)\\))?\\s*$")]
    private static partial Regex QuotedName();
}

/// <summary>Finds the camera on Linux: the capture node of each V4L2 device, from sysfs.</summary>
public static class V4l2Devices
{
    /// <summary>
    /// A USB camera shows up as two or more nodes - <c>/dev/video0</c> to
    /// capture, <c>/dev/video1</c> for metadata. The capture node is the one
    /// whose <c>index</c> is 0.
    /// </summary>
    public static IReadOnlyList<CameraDevice> Find(string sysClass = "/sys/class/video4linux", string dev = "/dev")
    {
        if (!Directory.Exists(sysClass))
        {
            return [];
        }

        var found = new List<(int Number, CameraDevice Device)>();

        foreach (var node in Directory.EnumerateDirectories(sysClass, "video*"))
        {
            var id = Path.GetFileName(node);
            if (!int.TryParse(id.AsSpan("video".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            var index = Read(Path.Combine(node, "index"));
            if (index is not null && index != "0")
            {
                continue;
            }

            var name = Read(Path.Combine(node, "name")) ?? id;
            found.Add((number, new CameraDevice(name, "v4l2", Path.Combine(dev, id).Replace('\\', '/'))));
        }

        return [.. found.OrderBy(entry => entry.Number).Select(entry => entry.Device)];
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>The FFmpeg command line that records (T-482).</summary>
public static class FfmpegCommand
{
    /// <summary>One file per minute, cut on the clock's minute.</summary>
    public const int SegmentSeconds = 60;

    /// <param name="askSize">
    /// Ask the camera for the size and rate itself. A camera that cannot give
    /// them refuses to open; the recorder then tries again without, and the
    /// picture is scaled instead.
    /// </param>
    public static IReadOnlyList<string> Record(
        CameraDevice device, string encoder, KioskCameraSettings settings, string outputPattern, bool askSize = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);

        var (width, height) = settings.Size;
        var fps = settings.FramesPerSecond;
        var bitrate = Bitrate(width, fps, settings.QualityLevel);

        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };

        switch (device.Format)
        {
            case "lavfi":
                args.AddRange(["-re", "-f", "lavfi", "-i", device.Input]);
                break;

            case "dshow":
                args.AddRange(["-f", "dshow", "-rtbufsize", "64M"]);
                if (askSize)
                {
                    args.AddRange(["-video_size", $"{width}x{height}", "-framerate", Text(fps)]);
                }

                args.AddRange(["-i", "video=" + device.Input]);
                break;

            default:
                args.AddRange(["-f", device.Format]);
                if (askSize)
                {
                    args.AddRange(["-video_size", $"{width}x{height}", "-framerate", Text(fps)]);
                }

                args.AddRange(["-i", device.Input]);
                break;
        }

        // No sound: only pictures are kept (docs/KIOSK_CAMERA.md).
        args.Add("-an");

        // A frame that is the same as the one before is dropped (T-487): an
        // empty kiosk is a still picture, and a still picture needs one frame
        // every few seconds, not ten a second - about a fifth of the disk.
        // At least one frame every five seconds is kept, so the time runs on.
        args.AddRange([
            "-vf",
            string.Create(
                CultureInfo.InvariantCulture,
                $"fps={fps},scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,mpdecimate=max={fps * StillSeconds},format=yuv420p"),
            "-fps_mode", "vfr",
        ]);

        args.AddRange(["-c:v", encoder]);
        args.AddRange(encoder switch
        {
            // Constant quality within a ceiling, and no look-ahead: with still
            // pictures dropped a frame may come once in five seconds, and a
            // look-ahead of forty frames would keep minutes of video in memory
            // - lost when the power goes (measured, docs/KIOSK_CAMERA.md).
            "libx264" => ["-preset", "veryfast", "-tune", "zerolatency", "-crf", Crf(settings.QualityLevel), "-maxrate", Kbit(bitrate), "-bufsize", Kbit(bitrate * 2)],
            _ => ["-b:v", Kbit(bitrate)],
        });

        // A key frame every two seconds, and one exactly where each minute
        // starts, so every file plays on its own.
        args.AddRange(["-g", Text(fps * 2), "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})"]);

        // Fragmented MP4, a fragment every two seconds and each written out at
        // once: a file cut short by a power cut still plays up to where it
        // stopped, even when a still picture makes the fragments tiny.
        args.AddRange([
            "-f", "segment",
            "-segment_time", Text(SegmentSeconds),
            "-segment_atclocktime", "1",
            "-segment_format", "mp4",
            "-segment_format_options", "movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=2000000:flush_packets=1",
            "-reset_timestamps", "1",
            "-strftime", "1",
            outputPattern,
        ]);

        return args;
    }

    /// <summary>The longest a still picture goes without a frame, in seconds.</summary>
    public const int StillSeconds = 5;

    /// <summary>
    /// The ceiling, in kbit/s (T-486): for 720p at 10 frames a second 600
    /// (Low), 1020 (Medium), 1800 (High) - a face stays a face at Medium.
    /// </summary>
    public static int Bitrate(int width, int fps, string quality = "Medium")
    {
        var atTen = width switch
        {
            <= 640 => 600,
            <= 1280 => 1200,
            _ => 2500,
        };

        var percent = quality switch
        {
            "Low" => 50,
            "High" => 150,
            _ => 85,
        };

        return Math.Max(200, atTen * fps / 10 * percent / 100);
    }

    /// <summary>x264's constant quality: lower is better and larger (measured, docs/KIOSK_CAMERA.md).</summary>
    public static string Crf(string quality) => quality switch
    {
        "Low" => "32",
        "High" => "23",
        _ => "28",
    };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Kbit(int kilobits) => Text(kilobits) + "k";
}
