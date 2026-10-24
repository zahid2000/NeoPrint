using MongoDB.Driver;

using NeoPrint.Application.Releases;
using NeoPrint.Domain.Releases;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IReleaseRepository" />
public sealed class ReleaseRepository : IReleaseRepository
{
    private readonly MongoContext _context;

    public ReleaseRepository(MongoContext context) => _context = context;

    public Task<Release?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Releases.Find(release => release.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<Release?> FindAsync(
        ReleaseComponent component, string platform, string keySet, string version, CancellationToken cancellationToken = default)
        => _context.Releases
            .Find(release => release.Component == component && release.Platform == platform
                             && release.KeySet == keySet && release.Version == version)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<Release>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.Releases.Find(FilterDefinition<Release>.Empty)
            .SortByDescending(release => release.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Release>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        => ids.Count == 0
            ? []
            : await _context.Releases.Find(release => ids.Contains(release.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListWithdrawnShaAsync(string platform, CancellationToken cancellationToken = default)
        => await _context.Releases
            .Find(release => release.Platform == platform && release.IsWithdrawn)
            .Project(release => release.Sha256)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(Release release, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Releases, release, cancellationToken);

    public Task UpdateAsync(Release release, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Releases, release, cancellationToken);
}

/// <inheritdoc cref="IReleaseRolloutRepository" />
public sealed class ReleaseRolloutRepository : IReleaseRolloutRepository
{
    private readonly MongoContext _context;

    public ReleaseRolloutRepository(MongoContext context) => _context = context;

    public Task<ReleaseRollout?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.ReleaseRollouts.Find(rollout => rollout.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<ReleaseRollout>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.ReleaseRollouts.Find(FilterDefinition<ReleaseRollout>.Empty)
            .SortByDescending(rollout => rollout.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ReleaseRollout>> ListForReleaseAsync(Guid releaseId, CancellationToken cancellationToken = default)
        => await _context.ReleaseRollouts.Find(rollout => rollout.ReleaseId == releaseId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ReleaseRollout>> ListOfferingAsync(string platform, CancellationToken cancellationToken = default)
        => await _context.ReleaseRollouts
            .Find(rollout => rollout.Platform == platform && rollout.Status == RolloutStatus.Active)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(ReleaseRollout rollout, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.ReleaseRollouts, rollout, cancellationToken);

    public Task UpdateAsync(ReleaseRollout rollout, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.ReleaseRollouts, rollout, cancellationToken);
}
