using System.Globalization;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Settings;

/// <summary>
/// Reads the tunable settings, caching them in Redis (plan section 2.4).
/// </summary>
/// <remarks>
/// Settings are read on nearly every request - payment timeouts, file limits,
/// queue sizes - so they are cached for a few minutes rather than fetched each
/// time. Saving a setting clears the entry, so a change takes effect at once.
/// </remarks>
public sealed class CachedSettingsReader : ISettingsReader
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    private readonly MongoContext _context;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CachedSettingsReader> _logger;

    public CachedSettingsReader(MongoContext context, IDistributedCache cache, ILogger<CachedSettingsReader> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var cached = await _cache.GetStringAsync(CacheKey(key), cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch (Exception exception)
        {
            // A cache outage must not take the API down; fall through to the
            // database.
            _logger.LogWarning(exception, "Reading setting '{Key}' from the cache failed.", key);
        }

        var setting = await _context.AdminSettings
            .Find(candidate => candidate.Key == key)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var value = setting?.Value
                    // A setting that has never been persisted still has a
                    // documented default, so a newly added key works before the
                    // seeder next runs.
                    ?? SettingKeys.Defaults.FirstOrDefault(definition => definition.Key == key)?.DefaultValue;

        if (value is not null)
        {
            try
            {
                await _cache.SetStringAsync(CacheKey(key), value, CacheOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Caching setting '{Key}' failed.", key);
            }
        }

        return value;
    }

    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken).ConfigureAwait(false);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken cancellationToken = default)
    {
        var value = await GetAsync(key, cancellationToken).ConfigureAwait(false);

        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>Drops a cached setting, so the next read sees the new value.</summary>
    public Task InvalidateAsync(string key, CancellationToken cancellationToken = default)
        => _cache.RemoveAsync(CacheKey(key), cancellationToken);

    /// <summary>
    /// The cache key for a setting.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the settings store clears the same
    /// entry this reader fills; two spellings of the key would mean a saved
    /// value that readers keep missing.
    /// </remarks>
    internal static string CacheKey(string key) => $"settings:{key}";
}
