using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Settings;

/// <inheritdoc cref="ISettingsStore" />
/// <remarks>
/// A write drops the cache entry rather than replacing it, so the next reader
/// fetches what is actually stored. Replacing it would put the new value in the
/// cache of the instance that did the write while the others kept the old one
/// for up to five minutes.
/// </remarks>
public sealed class MongoSettingsStore : ISettingsStore
{
    private readonly MongoContext _context;
    private readonly IDistributedCache _cache;
    private readonly IClock _clock;
    private readonly ILogger<MongoSettingsStore> _logger;

    public MongoSettingsStore(
        MongoContext context,
        IDistributedCache cache,
        IClock clock,
        ILogger<MongoSettingsStore> logger)
    {
        _context = context;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminSetting>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.AdminSettings
            .Find(FilterDefinition<AdminSetting>.Empty)
            .SortBy(setting => setting.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminSetting>> UpdateAsync(
        IReadOnlyDictionary<string, string> values,
        Guid? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var now = _clock.UtcNow;

        foreach (var (key, value) in values)
        {
            await _context.AdminSettings
                .UpdateOneAsync(
                    setting => setting.Key == key,
                    Builders<AdminSetting>.Update
                        .Set(setting => setting.Value, value)
                        .Set(setting => setting.UpdatedBy, updatedBy)
                        .Set(setting => setting.UpdatedAt, now),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        await DropFromCacheAsync(values.Keys, cancellationToken).ConfigureAwait(false);

        return await ListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DropFromCacheAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            try
            {
                await _cache
                    .RemoveAsync(CachedSettingsReader.CacheKey(key), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The value is already saved; a cache that could not be cleared
                // means readers see the old value for up to the cache lifetime,
                // which is worth a warning rather than failing the save.
                _logger.LogWarning(
                    exception, "Clearing the cached value of setting '{Key}' failed.", key);
            }
        }
    }
}
