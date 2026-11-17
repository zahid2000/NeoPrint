namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// How a kiosk's welcome screen is arranged (T-441), set by dragging in the
/// panel: the order of the three ways in, which of the footer's four features
/// show and in what order, and the side the picture is on. Kept with the
/// texts (<see cref="KioskBranding"/>): the default for every kiosk, and one
/// kiosk's own where it should differ.
/// </summary>
/// <remarks>
/// Only arranging, never free placing: the kiosk lays the screen out for
/// every size it runs at (1920x1080, 4:3, 5:4), and a fixed position would
/// fall off one of them. A way in cannot be hidden - each is a working
/// function, not a decoration.
/// </remarks>
public sealed class KioskScreenLayout
{
    /// <summary>The ways a file comes in, as the kiosk names its cards.</summary>
    public static readonly IReadOnlyList<string> CardKeys = ["usb", "phone", "code"];

    /// <summary>What the footer says the kiosk offers, as the kiosk names it.</summary>
    public static readonly IReadOnlyList<string> FeatureKeys = ["featureSizes", "featureColor", "featureFast", "featureSafe"];

    /// <summary>Every way in, in the order shown.</summary>
    public List<string> Cards { get; set; } = [.. CardKeys];

    /// <summary>The features shown, in their order; one left out is hidden.</summary>
    public List<string> Features { get; set; } = [.. FeatureKeys];

    /// <summary>The picture on the left of the big line; the right by default.</summary>
    public bool ImageLeft { get; set; }

    /// <summary>The kiosk's own arrangement, as it was before T-441.</summary>
    public static KioskScreenLayout Standard() => new();

    /// <summary>What is wrong with it, as message keys; empty when it can be kept.</summary>
    public IReadOnlyList<string> Problems()
    {
        var found = new List<string>();

        if (Cards is null || Cards.Count != CardKeys.Count || !CardKeys.All(Cards.Contains))
        {
            found.Add("kiosks.layoutCards");
        }

        if (Features is null
            || Features.Distinct(StringComparer.Ordinal).Count() != Features.Count
            || !Features.All(FeatureKeys.Contains))
        {
            found.Add("kiosks.layoutFeatures");
        }

        return found;
    }

    public bool SameAs(KioskScreenLayout other)
        => other is not null
           && Cards.SequenceEqual(other.Cards, StringComparer.Ordinal)
           && Features.SequenceEqual(other.Features, StringComparer.Ordinal)
           && ImageLeft == other.ImageLeft;
}
