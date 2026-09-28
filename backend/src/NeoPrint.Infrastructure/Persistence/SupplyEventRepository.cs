using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="ISupplyEventRepository" />
public sealed class SupplyEventRepository : ISupplyEventRepository
{
    private readonly MongoContext _context;

    public SupplyEventRepository(MongoContext context) => _context = context;

    public Task AddAsync(SupplyEvent supplyEvent, CancellationToken cancellationToken = default)
        => _context.SupplyEvents.InsertOneAsync(supplyEvent, cancellationToken);

    public async Task<IReadOnlyList<SupplyEvent>> ListForKioskAsync(Guid kioskId, int limit, CancellationToken cancellationToken = default)
        => await _context.SupplyEvents
            .Find(movement => movement.KioskId == kioskId)
            .SortByDescending(movement => movement.At)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
