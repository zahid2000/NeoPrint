using System.Text.Json.Serialization;
using System.Text.Json;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The last configuration the backend gave, on disk (plan section 7.7: "Son
/// konfiqurasiya lokal keşdə").
/// </summary>
/// <remarks>
/// A kiosk that restarts while the backend is out of reach still opens with
/// its printer, limits and prices, for as long as
/// <see cref="KioskConfiguration.CacheTtlHours"/> allows (plan section 7.3,
/// Degraded). Nothing secret is in it: the admin PIN is a PBKDF2 hash.
/// </remarks>
public sealed class ConfigurationCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IAppPaths _paths;

    public ConfigurationCache(IAppPaths paths) => _paths = paths;

    private string FilePath => Path.Combine(_paths.StateDirectory, "config.json");

    public void Save(KioskConfiguration configuration, DateTime fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Directory.CreateDirectory(_paths.StateDirectory);

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Cached(configuration, fetchedAt), Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>The cached configuration and when it was fetched; null when there is none or it cannot be read.</summary>
    public (KioskConfiguration Configuration, DateTime FetchedAt)? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var cached = JsonSerializer.Deserialize<Cached>(File.ReadAllText(FilePath), Json);
            return cached is null ? null : (cached.Configuration, cached.FetchedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    private sealed record Cached(KioskConfiguration Configuration, DateTime FetchedAt);
}
