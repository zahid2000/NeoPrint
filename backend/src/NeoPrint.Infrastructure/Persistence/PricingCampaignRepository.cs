using MongoDB.Driver;

using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Pricing;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IPricingCampaignRepository" />
public sealed class PricingCampaignRepository : IPricingCampaignRepository
{
    private readonly MongoContext _context;

    public PricingCampaignRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<PricingCampaign>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.PricingCampaigns.Find(FilterDefinition<PricingCampaign>.Empty)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PricingCampaign>> ListActiveAsync(CancellationToken cancellationToken = default)
        => await _context.PricingCampaigns.Find(c => c.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public Task<bool> AnyBoundaryAsync(DateTime after, DateTime until, CancellationToken cancellationToken = default)
        => _context.PricingCampaigns
            .Find(c => c.IsActive
                       && ((c.StartsAt > after && c.StartsAt <= until) || (c.EndsAt > after && c.EndsAt <= until)))
            .AnyAsync(cancellationToken);

    public Task<PricingCampaign?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PricingCampaigns.Find(c => c.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(PricingCampaign campaign, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.PricingCampaigns, campaign, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Row-versioned, as a profile is.</remarks>
    public Task UpdateAsync(PricingCampaign campaign, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.PricingCampaigns, campaign, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.PricingCampaigns.DeleteOneAsync(c => c.Id == id, cancellationToken);
}
