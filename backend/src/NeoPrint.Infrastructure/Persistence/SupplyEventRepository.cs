using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Contracts;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="ISupplyEventRepository" />
/// <remarks>
/// Every movement that is written asks for the kiosk's supplies to be judged
/// against their rules again (<see cref="SupplyLevelChanged"/>, plan section
/// 9.9): here, so that none of the places that move a counter - a finished
/// job, a test page, a refill, a recount, a finished service task - can
/// forget to. Inside a transaction the event leaves with the commit.
/// </remarks>
public sealed class SupplyEventRepository : ISupplyEventRepository
{
    private readonly MongoContext _context;
    private readonly IEventPublisher? _events;
    private readonly ICorrelationContext? _correlation;

    /// <param name="context">The database.</param>
    /// <param name="events">Absent in a host that publishes nothing.</param>
    /// <param name="correlation">The request the movement belongs to.</param>
    public SupplyEventRepository(MongoContext context, IEventPublisher? events = null, ICorrelationContext? correlation = null)
    {
        _context = context;
        _events = events;
        _correlation = correlation;
    }

    public async Task AddAsync(SupplyEvent supplyEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(supplyEvent);

        await _context.SupplyEvents.InsertOneAsync(supplyEvent, cancellationToken).ConfigureAwait(false);

        if (_events is not null)
        {
            await _events
                .PublishAsync(new SupplyLevelChanged(supplyEvent.KioskId, _correlation?.CorrelationId), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<SupplyEvent>> ListForKioskAsync(Guid kioskId, int limit, CancellationToken cancellationToken = default)
        => await _context.SupplyEvents
            .Find(movement => movement.KioskId == kioskId)
            .SortByDescending(movement => movement.At)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
