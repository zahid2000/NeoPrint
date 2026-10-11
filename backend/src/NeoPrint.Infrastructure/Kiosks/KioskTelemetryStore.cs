using System.Text.Json;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// The <c>kiosk_telemetry</c> time series (plan sections 5 and 7.9). The
/// collection is created by <see cref="MongoContext.ApplyRetentionAsync"/>,
/// which also gives it its lifetime (<c>Kiosk.TelemetryRetentionDays</c>).
/// </summary>
public sealed class KioskTelemetryStore : IKioskTelemetryStore
{
    private readonly MongoContext _context;

    public KioskTelemetryStore(MongoContext context) => _context = context;

    // A sample is stored on its own, outside any unit of work: a time series
    // cannot be written inside a multi-document transaction, and the handler
    // that reports telemetry opens none.
    private MongoSet<KioskTelemetrySample> Samples => _context.KioskTelemetry;

    public Task AddAsync(KioskTelemetrySample sample, CancellationToken cancellationToken = default)
        => Samples.InsertOneAsync(sample, cancellationToken);

    public async Task<KioskTelemetrySample?> LatestAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => await Samples
            .Find(sample => sample.KioskId == kioskId)
            .SortByDescending(sample => sample.Timestamp)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<KioskTelemetryPoint>> HistoryAsync(
        Guid kioskId, DateTime from, DateTime to, int sliceSeconds, CancellationToken cancellationToken = default)
    {
        const string System = nameof(KioskTelemetrySample.System);
        const string Network = nameof(KioskTelemetrySample.Network);
        const string Work = nameof(KioskTelemetrySample.Work);

        // Memory as a percentage is not stored - used and total are - so it is
        // worked out per sample before the slice is averaged.
        var memoryPercent = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$gt", new BsonArray { $"${System}.{nameof(TelemetrySystem.MemoryTotalBytes)}", 0 }),
            new BsonDocument("$multiply", new BsonArray
            {
                100,
                new BsonDocument("$divide", new BsonArray
                {
                    $"${System}.{nameof(TelemetrySystem.MemoryUsedBytes)}",
                    $"${System}.{nameof(TelemetrySystem.MemoryTotalBytes)}",
                }),
            }),
            BsonNull.Value,
        });

        var group = new BsonDocument
        {
            ["_id"] = new BsonDocument("$dateTrunc", new BsonDocument
            {
                ["date"] = $"${nameof(KioskTelemetrySample.Timestamp)}",
                ["unit"] = "second",
                ["binSize"] = Math.Max(1, sliceSeconds),
            }),
            ["cpu"] = new BsonDocument("$avg", $"${System}.{nameof(TelemetrySystem.CpuPercent)}"),
            ["memory"] = new BsonDocument("$avg", memoryPercent),
            ["systemDisk"] = new BsonDocument("$avg", $"${System}.{nameof(TelemetrySystem.SystemDiskFreeBytes)}"),
            ["spoolDisk"] = new BsonDocument("$avg", $"${System}.{nameof(TelemetrySystem.SpoolDiskFreeBytes)}"),
            ["latency"] = new BsonDocument("$avg", $"${Network}.{nameof(TelemetryNetwork.BackendLatencyMs)}"),
            ["queue"] = new BsonDocument("$avg", $"${Work}.{nameof(TelemetryWork.QueueLength)}"),
            ["samples"] = new BsonDocument("$sum", 1),
        };

        var slices = await Samples
            .Aggregate()
            .Match(sample => sample.KioskId == kioskId && sample.Timestamp >= from && sample.Timestamp <= to)
            .Group(group)
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. slices.Select(slice => new KioskTelemetryPoint(
                slice["_id"].ToUniversalTime(),
                Number(slice["cpu"]),
                Number(slice["memory"]),
                Number(slice["systemDisk"]),
                Number(slice["spoolDisk"]),
                Number(slice["latency"]),
                Number(slice["queue"]),
                slice["samples"].ToInt32())),
        ];
    }

    /// <summary>An average over no values is null: "no data" for that slice, not zero.</summary>
    private static double? Number(BsonValue value) => value.IsBsonNull ? null : Math.Round(value.ToDouble(), 2);
}

/// <inheritdoc cref="IKioskStatusCache" />
public sealed class RedisKioskStatusCache : IKioskStatusCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;

    public RedisKioskStatusCache(IConnectionMultiplexer redis) => _redis = redis;

    public Task SetAsync(KioskTelemetrySample sample, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return _redis.GetDatabase().StringSetAsync(Key(sample.KioskId), JsonSerializer.Serialize(sample, Json), lifetime);
    }

    public async Task<KioskTelemetrySample?> GetAsync(Guid kioskId, CancellationToken cancellationToken = default)
    {
        var stored = await _redis.GetDatabase().StringGetAsync(Key(kioskId)).ConfigureAwait(false);

        if (stored.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<KioskTelemetrySample>(stored.ToString(), Json);
        }
        catch (JsonException)
        {
            // Written by another build; the series has the sample.
            return null;
        }
    }

    private static RedisKey Key(Guid kioskId) => $"kiosk:status:{kioskId}";
}
