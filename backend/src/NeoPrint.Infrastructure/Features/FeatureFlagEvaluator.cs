using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Features;
using NeoPrint.Domain.Features;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Features;

/// <inheritdoc cref="IFeatureFlagRepository" />
public sealed class FeatureFlagRepository : IFeatureFlagRepository
{
    private readonly MongoContext _context;

    public FeatureFlagRepository(MongoContext context) => _context = context;

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.FeatureFlags
            .Find(FilterDefinition<FeatureFlag>.Empty)
            .SortBy(flag => flag.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<FeatureFlag?> FindByKeyAsync(string key, CancellationToken cancellationToken = default)
        => _context.FeatureFlags.Find(flag => flag.Key == key).FirstOrDefaultAsync(cancellationToken)!;

    public Task AddAsync(FeatureFlag flag, CancellationToken cancellationToken = default)
        => _context.FeatureFlags.InsertOneAsync(flag, cancellationToken: cancellationToken);

    public async Task UpdateAsync(FeatureFlag flag, CancellationToken cancellationToken = default)
    {
        await _context.FeatureFlags
            .ReplaceOneAsync(existing => existing.Id == flag.Id, flag, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Evaluates feature flags, with the flag set cached in Redis
/// (plan section 2.16).
/// </summary>
/// <remarks>
/// Flags are read on nearly every kiosk configuration fetch and on every mobile
/// start, and they change rarely, so the whole set is cached briefly. An
/// administrator saving a flag clears the entry, so a pilot switch takes effect
/// at once.
/// </remarks>
public sealed class CachedFeatureFlagEvaluator : IFeatureFlagEvaluator
{
    private const string CacheKey = "features:all";

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2),
    };

    private readonly IFeatureFlagRepository _flags;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CachedFeatureFlagEvaluator> _logger;

    public CachedFeatureFlagEvaluator(
        IFeatureFlagRepository flags, IDistributedCache cache, ILogger<CachedFeatureFlagEvaluator> logger)
    {
        _flags = flags;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool> IsEnabledAsync(
        string key, Guid? kioskId = null, Guid? userId = null, CancellationToken cancellationToken = default)
    {
        var flags = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var flag = flags.FirstOrDefault(candidate => candidate.Key == key);

        // An unknown key is treated as on: a flag is removed from the catalogue
        // once a feature is permanent, and the feature must not disappear with
        // it (plan section 2.16).
        return flag is null || FeatureFlagEvaluation.Evaluate(flag, kioskId, userId);
    }

    public async Task<IReadOnlyDictionary<string, bool>> EvaluateAllAsync(
        FeaturePlatform platform,
        Guid? kioskId = null,
        Guid? userId = null,
        CancellationToken cancellationToken = default)
    {
        var flags = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return flags
            .Where(flag => flag.Platforms.Count == 0 || flag.Platforms.Contains(platform))
            .ToDictionary(
                flag => flag.Key,
                flag => FeatureFlagEvaluation.Evaluate(flag, kioskId, userId),
                StringComparer.Ordinal);
    }

    /// <summary>Drops the cached flag set, so the next read sees the change.</summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(CacheKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The change is saved; without Redis it shows once the cached set expires.
            _logger.LogWarning(exception, "Dropping the cached feature flags failed.");
        }
    }

    private async Task<IReadOnlyList<FeatureFlag>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cached = await _cache.GetStringAsync(CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                var flags = System.Text.Json.JsonSerializer.Deserialize<List<FeatureFlag>>(cached);
                if (flags is not null)
                {
                    return flags;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Reading the cached feature flags failed.");
        }

        var loaded = await _flags.ListAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _cache.SetStringAsync(
                    CacheKey,
                    System.Text.Json.JsonSerializer.Serialize(loaded),
                    CacheOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Caching the feature flags failed.");
        }

        return loaded;
    }
}
