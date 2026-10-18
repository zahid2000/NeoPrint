namespace NeoPrint.Domain.Settings;

/// <summary>One link of the map's attribution line: its text and where it points.</summary>
public sealed record MapAttributionLink(string Label, string Url);

/// <summary>
/// The kiosks map's style and attribution (T-217, docs/DECISIONS.md
/// 2026-10-18): settings rather than code, so moving to an own tile server is
/// a settings change - no new app, no new panel build.
/// </summary>
public static class MapSettings
{
    /// <summary>OpenFreeMap's "Liberty" style: vector tiles, sprites and fonts from one host.</summary>
    public const string DefaultStyleUrl = "https://tiles.openfreemap.org/styles/liberty";

    /// <summary>What OpenFreeMap asks for, all three as links: <c>label|url</c>, separated by <c>;</c>.</summary>
    public const string DefaultAttribution =
        "OpenFreeMap|https://openfreemap.org;"
        + "© OpenMapTiles|https://www.openmaptiles.org/;"
        + "© OpenStreetMap contributors|https://www.openstreetmap.org/copyright";

    /// <summary>The licence of the data itself: an attribution without it is refused.</summary>
    public const string OpenStreetMapCopyright = "https://www.openstreetmap.org/copyright";

    /// <summary>A style address the map may load: absolute and over HTTPS.</summary>
    public static bool IsValidStyleUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// The attribution setting as links; null when any part is not
    /// <c>label|https-url</c>, or the OpenStreetMap copyright link is missing.
    /// </summary>
    public static IReadOnlyList<MapAttributionLink>? ParseAttribution(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var links = new List<MapAttributionLink>();

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('|', StringSplitOptions.TrimEntries);

            if (pieces.Length != 2 || pieces[0].Length == 0 || !IsValidStyleUrl(pieces[1]))
            {
                return null;
            }

            links.Add(new MapAttributionLink(pieces[0], pieces[1]));
        }

        return links.Exists(link => link.Url.StartsWith(OpenStreetMapCopyright, StringComparison.OrdinalIgnoreCase))
            ? links
            : null;
    }
}
