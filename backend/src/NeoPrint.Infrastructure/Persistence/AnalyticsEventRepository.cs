using MongoDB.Driver;

using NeoPrint.Application.Analytics;
using NeoPrint.Domain.Analytics;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IAnalyticsEventRepository" />
public sealed class AnalyticsEventRepository : IAnalyticsEventRepository
{
    private readonly MongoContext _context;

    public AnalyticsEventRepository(MongoContext context) => _context = context;

    public Task AddManyAsync(IReadOnlyCollection<AnalyticsEvent> events, CancellationToken cancellationToken = default)
        => _context.AnalyticsEvents.InsertManyAsync(events, cancellationToken);
}

/// <inheritdoc cref="IAnalyticsRejectionStore" />
public sealed class AnalyticsRejectionStore : IAnalyticsRejectionStore
{
    private readonly MongoContext _context;

    public AnalyticsRejectionStore(MongoContext context) => _context = context;

    public Task AddAsync(Guid kioskId, string day, AnalyticsRejections counts, string? lastUnknown, DateTime at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var update = Builders<AnalyticsRejectionDay>.Update
            .SetOnInsert(row => row.Day, day)
            .SetOnInsert(row => row.KioskId, kioskId)
            .Inc(row => row.UnknownEvent, counts.UnknownEvent)
            .Inc(row => row.NoSession, counts.NoSession)
            .Inc(row => row.OutOfTime, counts.OutOfTime)
            .Set(row => row.LastAt, at);

        if (lastUnknown is not null)
        {
            update = update.Set(row => row.LastUnknownName, lastUnknown);
        }

        return _context.AnalyticsRejections.UpdateOneAsync(
            Builders<AnalyticsRejectionDay>.Filter.Eq(row => row.Id, AnalyticsRejectionDay.IdOf(day, kioskId)),
            update,
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<IReadOnlyList<AnalyticsRejectionDay>> ListAsync(string fromDay, CancellationToken cancellationToken = default)
        => await _context.AnalyticsRejections
            .Find(Builders<AnalyticsRejectionDay>.Filter.Gte(row => row.Day, fromDay))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
