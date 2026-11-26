using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// What the welcome screen and the footer say, and the picture beside the big
/// line, as the panel set them (T-387): this kiosk's own, else the default.
/// </summary>
/// <param name="Texts">Key → language → text. A key or a language absent: the kiosk's own words.</param>
/// <param name="ImageVersion">The picture's version; null without one (the printer drawing stays).</param>
/// <param name="Layout">How the screen is arranged (T-441); null keeps the kiosk's own.</param>
public sealed record KioskBranding(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Texts,
    long? ImageVersion,
    KioskScreenLayout? Layout = null)
{
    /// <summary>The text set for this key in this language, if any.</summary>
    public string? Text(string key, string language)
        => Texts is not null
           && Texts.TryGetValue(key, out var languages)
           && languages.TryGetValue(language, out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}

/// <summary>
/// How the welcome screen is arranged, as the panel dragged it (T-441): the
/// ways in in their order, the footer's features shown in theirs, and
/// the side of the picture.
/// </summary>
public sealed record KioskScreenLayout(IReadOnlyList<string>? Cards, IReadOnlyList<string>? Features, bool ImageLeft)
{
    public static readonly IReadOnlyList<string> CardKeys = ["usb", "phone"];

    public static readonly IReadOnlyList<string> FeatureKeys = ["featureSizes", "featureColor", "featureFast", "featureSafe"];

    /// <summary>The kiosk's own arrangement, as before T-441.</summary>
    public static readonly KioskScreenLayout Standard = new(CardKeys, FeatureKeys, false);

    /// <summary>
    /// What the screen shows: this one, or the kiosk's own where it would leave
    /// a way in out or name something the kiosk does not know - an older or a
    /// newer backend never breaks the screen.
    /// </summary>
    public static KioskScreenLayout Effective(KioskScreenLayout? layout)
    {
        if (layout is null)
        {
            return Standard;
        }

        // The "Çap kodu" card a backend from before T-461 may still name is left out.
        var given = layout.Cards?.Where(CardKeys.Contains).ToList();
        var cards = given is { } kept && kept.Count == CardKeys.Count && CardKeys.All(kept.Contains) ? kept : CardKeys;
        var features = layout.Features is { } shown
            && shown.Distinct(StringComparer.Ordinal).Count() == shown.Count
            && shown.All(FeatureKeys.Contains)
                ? shown
                : FeatureKeys;

        return new KioskScreenLayout(cards, features, layout.ImageLeft);
    }

    /// <summary>The column of a way in on the screen, 0 or 1.</summary>
    public int ColumnOf(string card) => Math.Max(0, (Cards ?? CardKeys).ToList().IndexOf(card));
}

/// <summary>
/// The welcome screen's picture on disk (T-387), fetched once per version and
/// kept for when the backend is out of reach. Only a JPEG, PNG or WebP is kept,
/// read from its bytes; anything else is dropped and the drawing stays.
/// </summary>
public sealed class BrandingImageCache
{
    /// <summary>The largest picture taken: the panel sends it shrunk, well under this.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private readonly string _directory;
    private readonly ILogger _logger;

    public BrandingImageCache(string stateDirectory, ILogger logger)
    {
        _directory = Path.Combine(stateDirectory, "branding");
        _logger = logger;
    }

    /// <summary>Where this version is kept, once it is.</summary>
    public string PathFor(long version) => Path.Combine(_directory, $"hero-{version}");

    /// <summary>
    /// The file for this version: kept already, or fetched now. Null without a
    /// picture, or when it cannot be had - the drawing is shown then.
    /// </summary>
    public async Task<string?> GetAsync(long? version, Func<CancellationToken, Task<byte[]?>> fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        if (version is not { } wanted)
        {
            Prune(keep: null);
            return null;
        }

        var path = PathFor(wanted);
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            var bytes = await fetch(cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBytes || !IsPicture(bytes))
            {
                _logger.LogWarning("The welcome screen picture {Version} was not taken.", wanted);
                return null;
            }

            Directory.CreateDirectory(_directory);
            var partial = path + ".part";
            await File.WriteAllBytesAsync(partial, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
            Prune(keep: path);
            return path;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            // Out of reach now: the last one kept is better than none.
            _logger.LogWarning(exception, "The welcome screen picture {Version} could not be fetched yet.", wanted);
            return Latest();
        }
    }

    /// <summary>JPEG, PNG or WebP by the signature, as the backend checked it.</summary>
    public static bool IsPicture(ReadOnlySpan<byte> bytes)
        => (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
           || (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
           || (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8));

    private string? Latest()
        => Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "hero-*").Where(file => !file.EndsWith(".part", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;

    private void Prune(string? keep)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_directory))
        {
            if (!string.Equals(file, keep, StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(exception, "An old welcome screen picture is still in use.");
                }
            }
        }
    }
}
