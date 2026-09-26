using MongoDB.Driver;

using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IPricingProfileRepository" />
public sealed class PricingProfileRepository : IPricingProfileRepository
{
    private readonly MongoContext _context;

    public PricingProfileRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<PricingProfile>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.PricingProfiles.Find(FilterDefinition<PricingProfile>.Empty)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<PricingProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PricingProfiles.Find(p => p.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<PricingProfile?> FindDefaultAsync(CancellationToken cancellationToken = default)
        => _context.PricingProfiles.Find(p => p.IsDefault).FirstOrDefaultAsync(cancellationToken)!;

    public Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        var except = exceptId ?? Guid.Empty;

        return _context.PricingProfiles.Find(p => p.Name == trimmed && p.Id != except).AnyAsync(cancellationToken);
    }

    public Task AddAsync(PricingProfile profile, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.PricingProfiles, profile, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Row-versioned: two administrators editing the same profile must not
    /// silently overwrite each other's prices.
    /// </remarks>
    public Task UpdateAsync(PricingProfile profile, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PricingProfiles, profile, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PricingProfiles.DeleteOneAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountKiosksAsync(CancellationToken cancellationToken = default)
    {
        var kiosks = await _context.Kiosks
            .Find(k => k.PricingProfileId != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return kiosks
            .GroupBy(k => k.PricingProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
