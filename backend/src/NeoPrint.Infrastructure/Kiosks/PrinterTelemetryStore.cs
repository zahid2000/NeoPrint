using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// The <c>printer_telemetry</c> time series (plan sections 5 and 7.10), keyed
/// by kiosk and printer name. Created by <see cref="MongoContext.ApplyRetentionAsync"/>
/// with the same lifetime as kiosk telemetry.
/// </summary>
public sealed class PrinterTelemetryStore : IPrinterTelemetryStore
{
    private readonly MongoContext _context;

    public PrinterTelemetryStore(MongoContext context) => _context = context;

    // Stored on its own, outside any unit of work: a time series cannot be
    // written inside a multi-document transaction.
    private MongoSet<PrinterTelemetrySample> Samples => _context.PrinterTelemetry;

    public Task AddManyAsync(IReadOnlyCollection<PrinterTelemetrySample> samples, CancellationToken cancellationToken = default)
        => Samples.InsertManyAsync(samples, cancellationToken);

    public async Task<IReadOnlyList<PrinterTelemetrySample>> LatestAsync(
        Guid kioskId, DateTime since, CancellationToken cancellationToken = default)
    {
        var newest = await Samples
            .Aggregate()
            .Match(sample => sample.Printer.KioskId == kioskId && sample.Timestamp >= since)
            .Sort(Builders<PrinterTelemetrySample>.Sort.Descending(sample => sample.Timestamp))
            .Group(new BsonDocument
            {
                ["_id"] = $"${nameof(PrinterTelemetrySample.Printer)}.{nameof(PrinterKey.PrinterName)}",
                ["sample"] = new BsonDocument("$first", "$$ROOT"),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. newest.Select(group => BsonSerializer.Deserialize<PrinterTelemetrySample>(group["sample"].AsBsonDocument))];
    }

    public async Task<PrinterTelemetrySample?> LatestAsync(Guid kioskId, string printer, CancellationToken cancellationToken = default)
        => await Samples
            .Find(sample => sample.Printer.KioskId == kioskId && sample.Printer.PrinterName == printer)
            .SortByDescending(sample => sample.Timestamp)
            .Limit(1)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<PrinterTelemetrySample>> SlicesAsync(
        Guid kioskId, string printer, DateTime from, DateTime to, int sliceSeconds, CancellationToken cancellationToken = default)
    {
        var slices = await Samples
            .Aggregate()
            .Match(sample => sample.Printer.KioskId == kioskId
                             && sample.Printer.PrinterName == printer
                             && sample.Timestamp >= from
                             && sample.Timestamp <= to)
            .Sort(Builders<PrinterTelemetrySample>.Sort.Ascending(sample => sample.Timestamp))
            .Group(new BsonDocument
            {
                ["_id"] = new BsonDocument("$dateTrunc", new BsonDocument
                {
                    ["date"] = $"${nameof(PrinterTelemetrySample.Timestamp)}",
                    ["unit"] = "second",
                    ["binSize"] = Math.Max(1, sliceSeconds),
                }),
                ["sample"] = new BsonDocument("$last", "$$ROOT"),
            })
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. slices.Select(group => BsonSerializer.Deserialize<PrinterTelemetrySample>(group["sample"].AsBsonDocument))];
    }

    public async Task<IReadOnlyList<PrinterTelemetrySample>> RangeAsync(
        Guid kioskId, string printer, DateTime from, DateTime to, int take, CancellationToken cancellationToken = default)
    {
        // The newest "take" of the range, handed back oldest first.
        var newest = await Samples
            .Find(sample => sample.Printer.KioskId == kioskId
                            && sample.Printer.PrinterName == printer
                            && sample.Timestamp >= from
                            && sample.Timestamp <= to)
            .SortByDescending(sample => sample.Timestamp)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        newest.Reverse();

        return newest;
    }
}
