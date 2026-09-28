using System.Text.Json.Serialization;
using System.Text.Json;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>Where a job stands on this kiosk (plan step 3: job journal).</summary>
public enum JournalState
{
    /// <summary>In the spool, waiting for its turn.</summary>
    Queued = 0,

    /// <summary>Sent to the printer. If the kiosk dies here, nobody knows what came out.</summary>
    Printing = 1,

    /// <summary>The printer took it all.</summary>
    Printed = 2,

    /// <summary>Stopped with a reason.</summary>
    Interrupted = 3,

    /// <summary>Taken out of the queue before it printed.</summary>
    Cancelled = 4,
}

/// <summary>One job as this kiosk remembers it.</summary>
/// <param name="StatusReported">Whether the backend has heard the final state.</param>
public sealed record JournalEntry(
    Guid JobId,
    string JobCode,
    JournalState State,
    JobOptions Options,
    int RequiredSheets,
    int Attempts,
    int PrintedSheets,
    string? ErrorCode,
    bool StatusReported,
    DateTime QueuedAt,
    DateTime UpdatedAt)
{
    /// <summary>The spool name of the job's print file.</summary>
    public string SpoolName => SpoolNameFor(JobId);

    public static string SpoolNameFor(Guid jobId) => $"job-{jobId:N}.pdf";
}

/// <summary>
/// The kiosk's record of every job it holds, written before each step it
/// takes (plan step 3; docs/FAILURE_RECOVERY.md).
/// </summary>
/// <remarks>
/// <para>
/// "Printing" is written before a single page reaches the printer. A kiosk
/// that restarts and finds a job still marked Printing cannot know what came
/// out, so it never sends it again: it reports the job interrupted and lets
/// the rules for an interrupted job take over. That is how a power cut in the
/// middle of a job cannot become a double print.
/// </para>
/// <para>
/// It also holds the reports the backend has not heard yet, so a job that
/// finished while the link was down is still reported once it is back.
/// </para>
/// <para>
/// Written as a whole file, to a temporary name and moved over the old one,
/// so a crash mid-write leaves the previous journal intact.
/// </para>
/// </remarks>
public sealed class JobJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;
    private readonly object _gate = new();
    private Dictionary<Guid, JournalEntry>? _entries;

    public JobJournal(IAppPaths paths) => _paths = paths;

    private string FilePath => Path.Combine(_paths.StateDirectory, "journal.json");

    public IReadOnlyList<JournalEntry> All()
    {
        lock (_gate)
        {
            return [.. Load().Values.OrderBy(entry => entry.QueuedAt)];
        }
    }

    public JournalEntry? Find(Guid jobId)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(jobId);
        }
    }

    /// <summary>Adds or replaces an entry, and writes the journal before returning.</summary>
    public void Save(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            var entries = Load();
            entries[entry.JobId] = entry;
            Write(entries);
        }
    }

    public void Remove(Guid jobId)
    {
        lock (_gate)
        {
            var entries = Load();

            if (entries.Remove(jobId))
            {
                Write(entries);
            }
        }
    }

    /// <summary>Forgets everything: the device was detached.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];

            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
    }

    private Dictionary<Guid, JournalEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = [];

        if (!File.Exists(FilePath))
        {
            return _entries;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(FilePath), Json) ?? [];
            _entries = stored.ToDictionary(entry => entry.JobId);
        }
        catch (JsonException)
        {
            // A journal that cannot be read is kept aside for whoever looks
            // into it, and the kiosk starts clean rather than not at all.
            File.Move(FilePath, FilePath + $".unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }

        return _entries;
    }

    private void Write(Dictionary<Guid, JournalEntry> entries)
    {
        Directory.CreateDirectory(_paths.StateDirectory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries.Values.ToList(), Json));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
