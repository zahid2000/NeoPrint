using Avalonia.Media;

namespace NeoPrint.Kiosk.Styles;

/// <summary>
/// The design tokens a converter needs, as brushes.
/// </summary>
/// <remarks>
/// <para>
/// <c>Styles/Tokens.axaml</c> is the source: it is what the views bind to. A
/// value converter runs outside the visual tree and has no element to resolve
/// a <c>DynamicResource</c> against, so the few colours it needs are mirrored
/// here.
/// </para>
/// <para>
/// A mirror can drift, so it is not left to discipline:
/// <c>TokenParityTests</c> reads Tokens.axaml and fails if any value below
/// stops matching it (docs/DESIGN_SYSTEM.md section 8).
/// </para>
/// </remarks>
public static class KioskTokens
{
    /// <summary>The key each brush mirrors in Tokens.axaml, for the parity test.</summary>
    public static readonly IReadOnlyDictionary<string, string> Mirrored =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Success500"] = "#16A34A",
            ["Neutral400"] = "#9AA3B0",
            ["Brand600"] = "#1F4CBF",
        };

    /// <summary>A job that is printing right now.</summary>
    public static readonly IBrush Success = Brush("Success500");

    /// <summary>A job that is waiting, or a link that is down.</summary>
    public static readonly IBrush Idle = Brush("Neutral400");

    /// <summary>The primary brand colour at the kiosk's raised contrast.</summary>
    public static readonly IBrush Brand = Brush("Brand600");

    private static IBrush Brush(string key)
        => new SolidColorBrush(Color.Parse(Mirrored[key]));
}
