using System.Text.Json;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// Counts the kiosk UI's crashes and decides when it starts in safe mode
/// (plan section 7.12, step 5; <c>Kiosk.CrashLoopThreshold</c>).
/// </summary>
/// <remarks>
/// <para>
/// The UI counts for itself, because it is not always the agent that
/// restarts it: on Windows it is Shell Launcher, on Linux systemd or the
/// agent. A run that never reached <see cref="Stop"/> ended without the UI
/// saying goodbye and counts as a crash - unless the machine has booted
/// since it started: then it was a reboot or a power cut, which a crash loop
/// is not (the kiosk reports those separately).
/// </para>
/// <para>
/// Safe mode stays on until somebody clears it on site (the admin menu) or
/// from the panel: restarting into the same fault would only loop again.
/// </para>
/// <para>
/// Written as a whole file, to a temporary name and moved over the old one,
/// like the job journal: a crash mid-write leaves the previous record.
/// </para>
/// </remarks>
public sealed class CrashLoopGuard
{
    /// <summary>The window crashes are counted over (plan section 7.12: ten minutes).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly int _threshold;
    private readonly Func<DateTime> _bootedAt;

    /// <param name="bootedAt">When the machine last started; the uptime counter by default.</param>
    public CrashLoopGuard(IAppPaths paths, IClock clock, int threshold = 3, Func<DateTime>? bootedAt = null)
    {
        _paths = paths;
        _clock = clock;
        _threshold = Math.Max(1, threshold);
        _bootedAt = bootedAt ?? (() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private string FilePath => Path.Combine(_paths.StateDirectory, "runs.json");

    /// <summary>Since when the kiosk is in safe mode; null when it is not.</summary>
    public DateTime? SafeModeSince => Read().SafeModeSince;

    /// <summary>The last time a run ended without closing cleanly, within <see cref="Window"/>; telemetry reports it.</summary>
    public DateTime? LastCrashAt => Read().Crashes is { Count: > 0 } crashes ? crashes.Max() : null;

    /// <summary>Whether the run before this one ended without closing cleanly; known after <see cref="Start"/>.</summary>
    public bool CrashedLastRun { get; private set; }

    /// <summary>
    /// Called once at start: counts the last run if it did not end cleanly,
    /// and says whether this run is in safe mode.
    /// </summary>
    public bool Start()
    {
        var now = _clock.UtcNow;
        var record = Read();

        var crashes = record.Crashes.Where(at => now - at <= Window).ToList();

        if (record.Running && record.StartedAt is { } started && started > _bootedAt())
        {
            // Noticed now, a restart delay after it happened.
            crashes.Add(now);
            CrashedLastRun = true;
        }

        var safeModeSince = record.SafeModeSince ?? (crashes.Count >= _threshold ? now : null);

        Write(new RunRecord(true, now, crashes, safeModeSince));

        return safeModeSince is not null;
    }

    /// <summary>The UI is closing on purpose: this run is not a crash.</summary>
    public void Stop() => Write(Read() with { Running = false });

    /// <summary>Leaves safe mode; the crash count starts again.</summary>
    public void Clear() => Write(Read() with { Crashes = [], SafeModeSince = null });

    private RunRecord Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(FilePath), Json) ?? RunRecord.Empty
                : RunRecord.Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A damaged record is no reason to stop the kiosk; it starts
            // counting again.
            return RunRecord.Empty;
        }
    }

    private void Write(RunRecord record)
    {
        Directory.CreateDirectory(_paths.StateDirectory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record RunRecord(bool Running, DateTime? StartedAt, List<DateTime> Crashes, DateTime? SafeModeSince)
    {
        public static RunRecord Empty => new(false, null, [], null);
    }
}
