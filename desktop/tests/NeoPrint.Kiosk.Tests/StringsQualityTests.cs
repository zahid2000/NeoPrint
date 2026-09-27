using System.Text.RegularExpressions;

using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's text is in the language it claims to be
/// (docs/DESIGN_SYSTEM.md section 6).
/// </summary>
/// <remarks>
/// The same rules run on the admin panel (translations.spec.ts) and the mobile
/// app (l10n_test.dart). Each check lists every offender, so one run shows all
/// there is to fix.
/// </remarks>
public sealed partial class StringsQualityTests
{
    /// <summary>Brands, abbreviations and words Azerbaijani shares with English.</summary>
    private static readonly HashSet<string> SameInEveryLanguage = new(StringComparer.Ordinal)
    {
        "NeoPrint", "Email", "PDF", "QR", "PIN", "USB", "OK", "A4", "A3",
        "Azərbaycan", "English", "Русский",
    };

    [GeneratedRegex(
        @"\b(token|tokens|flag|flags|feature|features|enrollment|dashboard|default|override|overrides|rollout|provisioning|endpoint|mirror|primary|secondary|hash|job|jobs|queue|upload|download|error|network)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnglishInAzerbaijani();

    [GeneratedRegex(@"[Ѐ-ӿ]")]
    private static partial Regex Cyrillic();

    [GeneratedRegex("[əƏğĞıİşŞçÇöÖüÜ]")]
    private static partial Regex AzerbaijaniLetters();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string Words(string text) => Placeholder().Replace(text, string.Empty).Trim();

    private static string Placeholders(string text)
        => string.Join(',', Placeholder().Matches(text).Select(match => match.Groups[1].Value).Distinct().Order());

    [Fact]
    public void Every_language_has_every_key()
    {
        foreach (var language in Strings.SupportedLanguages)
        {
            Strings.KeysOf(language).Should().BeEquivalentTo(Strings.AllKeys,
                $"a key missing from {language} would show the Azerbaijani text instead");
        }
    }

    [Fact]
    public void The_placeholders_are_the_same_in_every_language()
    {
        var offenders = Strings.AllKeys
            .SelectMany(key => Strings.SupportedLanguages
                .Where(language => Placeholders(Strings.Get(language, key)) != Placeholders(Strings.Get("az", key)))
                .Select(language => $"{language}.{key}"))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Azerbaijani_text_is_in_Azerbaijani()
    {
        var offenders = Strings.AllKeys
            .Select(key => (key, value: Strings.Get("az", key)))
            .Where(entry => EnglishInAzerbaijani().IsMatch(entry.value)
                            || Cyrillic().IsMatch(entry.value)
                            || (Words(entry.value).Length >= 4
                                && !SameInEveryLanguage.Contains(Words(entry.value))
                                && Words(entry.value) == Words(Strings.Get("en", entry.key))))
            .Select(entry => $"{entry.key}: {entry.value}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Russian_text_is_in_Cyrillic()
    {
        var offenders = Strings.AllKeys
            .Select(key => (key, value: Strings.Get("ru", key)))
            .Where(entry => Words(entry.value).Count(char.IsLetter) >= 3
                            && !SameInEveryLanguage.Contains(Words(entry.value))
                            && !Cyrillic().IsMatch(entry.value))
            .Select(entry => $"{entry.key}: {entry.value}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void English_text_has_no_Azerbaijani_or_Russian_letters()
    {
        var offenders = Strings.AllKeys
            .Select(key => (key, value: Strings.Get("en", key)))
            .Where(entry => !SameInEveryLanguage.Contains(entry.value)
                            && (Cyrillic().IsMatch(entry.value) || AzerbaijaniLetters().IsMatch(entry.value)))
            .Select(entry => $"{entry.key}: {entry.value}")
            .ToList();

        offenders.Should().BeEmpty();
    }
}
