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
