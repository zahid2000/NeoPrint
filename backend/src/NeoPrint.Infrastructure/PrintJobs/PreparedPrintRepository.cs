using MongoDB.Driver;

using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.PrintJobs;

/// <inheritdoc cref="IPreparedPrintRepository" />
public sealed class PreparedPrintRepository : IPreparedPrintRepository
{
    private readonly MongoContext _context;

    public PreparedPrintRepository(MongoContext context) => _context = context;

    public Task AddAsync(PreparedPrint prepared, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.PreparedPrints, prepared, cancellationToken);

    public Task<PreparedPrint?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PreparedPrints.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(PreparedPrint prepared, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PreparedPrints, prepared, cancellationToken);

    public async Task<IReadOnlyList<PreparedPrint>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => await _context.PreparedPrints
            .Find(p => p.UserId == userId && p.RemovedAt == null)
            .SortByDescending(p => p.CreatedAt)
            .Limit(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
