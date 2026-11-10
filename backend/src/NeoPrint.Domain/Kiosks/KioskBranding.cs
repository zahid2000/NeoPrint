using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// What a kiosk's welcome screen and footer say, and the picture beside the
/// big line (T-387; <c>docs/DECISIONS.md</c> 2026-11-10): set in the panel for
/// every kiosk, and for one kiosk where it should differ.
/// </summary>
/// <remarks>
/// One document for the default (<see cref="DefaultId"/>) and one for each
/// kiosk with its own words, keyed by the kiosk's id. A text left empty -
/// in one language or in all three - falls back to the default, and past it
/// to the kiosk's own built-in words: nothing set is never a blank screen.
/// </remarks>
public sealed class KioskBranding : Entity
{
    /// <summary>
    /// The id of the document every kiosk falls back to. Not <see cref="Guid.Empty"/>:
    /// the database takes an empty id for "none yet" and makes up a new one.
    /// </summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000387");

    /// <summary>The longest text the screen has room for.</summary>
    public const int MaxLength = 160;

    /// <summary>The texts that can be set, as the kiosk names them.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "heroTitle", "heroSubtitle",
        "usbTitle", "usbHint", "phoneTitle", "phoneHint", "codeTitle", "codeHint",
        "featureSizes", "featureSizesHint", "featureColor", "featureColorHint",
        "featureFast", "featureFastHint", "featureSafe", "featureSafeHint",
        "hotline",
    ];

    /// <summary>The languages a text is written in (plan section 2.1).</summary>
    public static readonly IReadOnlyList<string> Languages = ["az", "en", "ru"];

    /// <summary>Key → language → text; only what was written is kept.</summary>
    public Dictionary<string, Dictionary<string, string>> Texts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The picture beside the big line; null leaves the printer drawing.</summary>
    public KioskPhoto? Image { get; private set; }

    public DateTime UpdatedAt { get; set; }

    public bool IsDefault => Id == DefaultId;

    /// <summary>
    /// Puts these texts in place of the ones there. Empty ones are dropped:
    /// that text falls back again.
    /// </summary>
    /// <returns>The keys whose text is not one the kiosk knows or is too long; nothing is changed then.</returns>
    public IReadOnlyList<string> SetTexts(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> texts, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var wrong = new List<string>();
        var kept = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var (key, byLanguage) in texts)
        {
            if (!Keys.Contains(key, StringComparer.Ordinal))
            {
                wrong.Add(key);
                continue;
            }

            foreach (var (language, value) in byLanguage)
            {
                var text = value?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (!Languages.Contains(language, StringComparer.Ordinal) || text.Length > MaxLength)
                {
                    wrong.Add(key);
                    continue;
                }

                if (!kept.TryGetValue(key, out var languages))
                {
                    kept[key] = languages = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                languages[language] = text;
            }
        }

        if (wrong.Count > 0)
        {
            return wrong.Distinct(StringComparer.Ordinal).ToList();
        }

        Texts = kept;
        UpdatedAt = utcNow;
        return [];
    }

    /// <summary>The text for this key and language, if one was written.</summary>
    public string? Text(string key, string language)
        => Texts.TryGetValue(key, out var languages) && languages.TryGetValue(language, out var text) ? text : null;

    /// <summary>Puts a new picture in place; hands back the one it replaces, for its object to be deleted.</summary>
    public KioskPhoto? ReplaceImage(FileObject stored, string contentType, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var previous = Image;
        Image = new KioskPhoto
        {
            Object = stored,
            ContentType = contentType,
            // Never repeats, even after a removal: the kiosk caches by it.
            Version = Math.Max(utcNow.Ticks, (previous?.Version ?? 0) + 1),
            UpdatedAt = utcNow,
        };
        UpdatedAt = utcNow;

        return previous;
    }

    /// <summary>Takes the picture away; hands back what was there, if anything.</summary>
    public KioskPhoto? RemoveImage(DateTime utcNow)
    {
        var previous = Image;
        Image = null;
        UpdatedAt = utcNow;
        return previous;
    }
}
