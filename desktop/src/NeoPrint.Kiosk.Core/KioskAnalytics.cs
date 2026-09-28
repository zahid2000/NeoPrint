using System.Text.Json;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// What the kiosk tells the pilot report (plan section 2.17): screens,
/// files, conversions, prices, orders, abandoned visits, printed jobs.
/// </summary>
/// <remarks>
/// Properties carry kinds and counts - a format, a page count, a message key -
/// never a file name or anything about the person; the backend keeps only
/// the properties its catalogue lists, so this is checked twice.
/// </remarks>
public interface IKioskAnalytics
{
    /// <summary>A new customer at the screen: the events that follow belong together.</summary>
    string NewSession();

    void Track(string name, IReadOnlyDictionary<string, string?>? props = null, string? sessionId = null);
}

/// <summary>For tests and for a kiosk that sends nothing.</summary>
public sealed class NoAnalytics : IKioskAnalytics
{
    public static readonly NoAnalytics Instance = new();

    public string NewSession() => Guid.NewGuid().ToString("N");

    public void Track(string name, IReadOnlyDictionary<string, string?>? props = null, string? sessionId = null)
    {
    }
}

/// <summary>One event, as it waits to be sent.</summary>
public sealed record AnalyticsRecord(string Event, string SessionId, DateTime At, Dictionary<string, string?> Props);

/// <summary>
/// The kiosk's analytics, kept on disk until the backend has them
/// (<c>state/analytics-*.json*</c>): a kiosk offline for an afternoon still
/// reports its afternoon.
/// </summary>
/// <remarks>
/// <para>
/// Sent in batches. A batch is taken out of the queue with its own key and
/// kept until the backend says it has it; a batch that failed goes again with
/// the same key (<c>Idempotency-Key</c>), so a lost answer does not store it
/// twice.
/// </para>
/// <para>
/// At most <see cref="MaxQueued"/> events wait; beyond that the oldest go, so
/// a kiosk that never reaches the backend does not fill its disk.
/// </para>
/// </remarks>
public sealed class AnalyticsQueue : IKioskAnalytics
{
    public const int MaxQueued = 5000;

    public const int BatchSize = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly object _gate = new();

    private string _session = Guid.NewGuid().ToString("N");

    public AnalyticsQueue(IAppPaths paths, IClock clock)
    {
        _paths = paths;
        _clock = clock;
    }

    /// <summary>One event per line, appended: tracking costs a line, not a rewrite.</summary>
    private string PendingPath => Path.Combine(_paths.StateDirectory, "analytics-pending.jsonl");

    /// <summary>The batch on its way, with its key, until the backend has it.</summary>
    private string BatchPath => Path.Combine(_paths.StateDirectory, "analytics-batch.json");

    /// <summary>Events waiting, the batch in flight included.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return ReadPending().Count + (ReadBatch()?.Events.Count ?? 0);
            }
        }
    }

    public string NewSession()
    {
        lock (_gate)
        {
            _session = Guid.NewGuid().ToString("N");
            return _session;
        }
    }

    public void Track(string name, IReadOnlyDictionary<string, string?>? props = null, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            var record = new AnalyticsRecord(
                name,
                sessionId ?? _session,
                _clock.UtcNow,
                props is null ? [] : new Dictionary<string, string?>(props, StringComparer.Ordinal));

            try
            {
                Directory.CreateDirectory(_paths.StateDirectory);
                File.AppendAllText(PendingPath, JsonSerializer.Serialize(record, Json) + "\n");
            }
            catch (IOException)
            {
                // Analytics are not worth a failed order: the event is lost.
            }
        }
    }

    /// <summary>Sends what is waiting, one batch at a time, until the queue is empty or a send fails.</summary>
    /// <param name="send">Sends a batch under its key; throws when it did not arrive.</param>
    /// <returns>How many events were delivered.</returns>
    public async Task<int> FlushAsync(
        Func<string, IReadOnlyList<AnalyticsRecord>, CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        var delivered = 0;

        while (true)
        {
            Batch? batch;

            lock (_gate)
            {
                batch = ReadBatch() ?? NextBatch();
            }

            if (batch is null)
            {
                return delivered;
            }

            // Outside the lock: tracking goes on while a batch is on its way.
            await send(batch.Key, batch.Events, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (ReadBatch()?.Key == batch.Key)
                {
                    File.Delete(BatchPath);
                }
            }

            delivered += batch.Events.Count;
        }
    }

    /// <summary>
    /// Moves the oldest events into a new batch file and keeps the rest -
    /// only the newest <see cref="MaxQueued"/> of them, so a kiosk that never
    /// reaches the backend does not fill its disk.
    /// </summary>
    private Batch? NextBatch()
    {
        var pending = ReadPending();

        if (pending.Count == 0)
        {
            return null;
        }

        if (pending.Count > MaxQueued)
        {
            pending = pending.GetRange(pending.Count - MaxQueued, MaxQueued);
        }

        var take = Math.Min(BatchSize, pending.Count);
        var batch = new Batch(Guid.NewGuid().ToString("N"), pending.GetRange(0, take));

        // The batch is written first: a crash between the two writes sends
        // these events once more, never loses them.
        Write(BatchPath, JsonSerializer.Serialize(batch, Json));
        Write(PendingPath, string.Concat(pending.Skip(take).Select(record => JsonSerializer.Serialize(record, Json) + "\n")));

        return batch;
    }

    private List<AnalyticsRecord> ReadPending()
    {
        if (!File.Exists(PendingPath))
        {
            return [];
        }

        var records = new List<AnalyticsRecord>();

        foreach (var line in File.ReadLines(PendingPath))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<AnalyticsRecord>(line, Json) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // A line cut short by a power cut: that one event is lost.
            }
        }

        return records;
    }

    private Batch? ReadBatch()
    {
        try
        {
            return File.Exists(BatchPath) ? JsonSerializer.Deserialize<Batch>(File.ReadAllText(BatchPath), Json) : null;
        }
        catch (JsonException)
        {
            File.Delete(BatchPath);
            return null;
        }
    }

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(_paths.StateDirectory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Batch(string Key, List<AnalyticsRecord> Events);
}
