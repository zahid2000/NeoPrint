using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Reads the tunable settings (plan section 2.4).
/// </summary>
/// <remarks>
/// Cached in Redis and refreshed when an administrator saves a change, so a
/// handler can read a setting without a database round trip.
/// </remarks>
public interface ISettingsReader
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default);

    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reading and writing the settings collection itself
/// (plan section 2.4).
/// </summary>
/// <remarks>
/// Separate from <see cref="ISettingsReader"/>, which answers "what is this one
/// value" on a hot path and caches the answer. This is what the admin panel
/// uses: the whole set, with defaults and metadata, and the writes.
/// </remarks>
public interface ISettingsStore
{
    Task<IReadOnlyList<AdminSetting>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies changed values and drops their cache entries.
    /// </summary>
    /// <returns>The settings as they now stand.</returns>
    Task<IReadOnlyList<AdminSetting>> UpdateAsync(
        IReadOnlyDictionary<string, string> values,
        Guid? updatedBy,
        CancellationToken cancellationToken = default);
}
