using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IKioskLogRepository" />
public sealed class KioskLogRepository : IKioskLogRepository
{
    private readonly MongoContext _context;

    public KioskLogRepository(MongoContext context) => _context = context;

    public Task AddAsync(KioskLog log, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.KioskLogs, log, cancellationToken);

    public Task<KioskLog?> FindAsync(Guid kioskId, Guid id, CancellationToken cancellationToken = default)
        => _context.KioskLogs.Find(log => log.Id == id && log.KioskId == kioskId).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<KioskLog>> ListAsync(Guid kioskId, int limit, CancellationToken cancellationToken = default)
        => await _context.KioskLogs.Find(log => log.KioskId == kioskId)
            .SortByDescending(log => log.CreatedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<KioskLog>> ListExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken = default)
        => await _context.KioskLogs.Find(log => log.ExpiresAt < now)
            .Limit(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.KioskLogs.DeleteOneAsync(log => log.Id == id, cancellationToken);
}
