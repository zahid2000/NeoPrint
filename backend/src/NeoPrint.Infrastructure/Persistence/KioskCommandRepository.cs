using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IKioskCommandRepository" />
public sealed class KioskCommandRepository : IKioskCommandRepository
{
    private readonly MongoContext _context;

    public KioskCommandRepository(MongoContext context) => _context = context;

    public Task<KioskCommand?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.KioskCommands.Find(command => command.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(KioskCommand command, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.KioskCommands, command, cancellationToken);

    public Task UpdateAsync(KioskCommand command, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.KioskCommands, command, cancellationToken);

    public async Task<IReadOnlyList<KioskCommand>> ListOpenAsync(Guid kioskId, CancellationToken cancellationToken = default)
        => await _context.KioskCommands
            .Find(command => command.KioskId == kioskId
                             && (command.Status == KioskCommandStatus.Sent || command.Status == KioskCommandStatus.Acknowledged))
            .SortBy(command => command.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<KioskCommand>> ListRecentAsync(Guid kioskId, int take, CancellationToken cancellationToken = default)
        => await _context.KioskCommands
            .Find(command => command.KioskId == kioskId)
            .SortByDescending(command => command.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
