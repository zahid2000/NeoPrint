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
