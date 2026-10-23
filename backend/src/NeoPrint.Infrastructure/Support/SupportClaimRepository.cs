using MongoDB.Driver;

using NeoPrint.Application.Support;
using NeoPrint.Domain.Support;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Support;

public sealed class SupportClaimRepository : ISupportClaimRepository
{
    private readonly MongoContext _context;

    public SupportClaimRepository(MongoContext context) => _context = context;

    public Task AddAsync(SupportClaim claim, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.SupportClaims, claim, cancellationToken);

    public Task<SupportClaim?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.SupportClaims.Find(c => c.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(SupportClaim claim, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.SupportClaims, claim, cancellationToken);

    public async Task<IReadOnlyList<SupportClaim>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await _context.SupportClaims.Find(c => c.JobId == jobId).SortByDescending(c => c.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SupportClaim>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default)
        => await _context.SupportClaims.Find(c => c.UserId == userId).SortByDescending(c => c.CreatedAt).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<long> CountOpenForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => _context.SupportClaims.CountDocumentsAsync(c => c.JobId == jobId && c.Status == ClaimStatus.Open, cancellationToken);

    public async Task<(IReadOnlyList<SupportClaim> Items, long Total)> SearchAsync(
        IReadOnlyCollection<ClaimStatus>? statuses, Guid? jobId, int skip, int take, CancellationToken cancellationToken = default, IReadOnlyCollection<Guid>? kiosks = null)
    {
        var builder = Builders<SupportClaim>.Filter;
        var query = builder.Empty;

        if (kiosks is not null)
        {
            query &= builder.In(c => c.KioskId, kiosks.Select(id => (Guid?)id));
        }

        if (statuses is { Count: > 0 })
        {
            query &= builder.In(c => c.Status, statuses);
        }

        if (jobId is { } job)
        {
            query &= builder.Eq(c => c.JobId, job);
        }

        var items = await _context.SupportClaims.Find(query).SortByDescending(c => c.CreatedAt).Skip(skip).Limit(take)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var total = await _context.SupportClaims.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}
