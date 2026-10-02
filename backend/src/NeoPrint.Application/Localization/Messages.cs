using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Localization;

/// <summary>
/// The backend's text for people, in the three interface languages (plan
/// section 2.1: az default, en, ru).
/// </summary>
/// <remarks>
/// <para>
/// The catalogs are the embedded <c>Localization/messages.{language}.json</c>
/// files: flat <c>key → text</c>, with <c>{name}</c> placeholders. Azerbaijani
/// is complete by rule and is what any gap falls back to; the parity test
/// keeps English and Russian complete too.
/// </para>
/// <para>
/// The language is <see cref="Current"/> - set per request by the API from
/// <c>Accept-Language</c> or the signed-in user's own language - unless the
/// caller names one, as an email does for its recipient. Not
/// <c>CultureInfo</c>: the backend runs with invariant globalization.
/// </para>
/// </remarks>
public static class Messages
{
    public const string DefaultLanguage = "az";

    public static readonly IReadOnlyList<string> Languages = ["az", "en", "ru"];

    private static readonly AsyncLocal<string?> CurrentLanguage = new();

    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Catalogs =
        new(Load);

    /// <summary>The language of the text written out on this call path.</summary>
    public static string Current
    {
        get => CurrentLanguage.Value ?? DefaultLanguage;
        set => CurrentLanguage.Value = Normalise(value);
    }

    /// <summary>
    /// One of the supported languages for a tag such as <c>ru</c>, <c>en-US</c>
    /// or <c>az-Latn-AZ</c>; null for anything else.
    /// </summary>
    public static string? Supported(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var primary = tag.Trim().Split('-', '_')[0].ToLowerInvariant();

        return Languages.Contains(primary) ? primary : null;
    }

    /// <summary>The keys of one language's catalog, for the parity checks.</summary>
    public static IReadOnlyDictionary<string, string> Catalog(string language)
        => Catalogs.Value.TryGetValue(language, out var catalog)
            ? catalog
            : FrozenDictionary<string, string>.Empty;

    /// <summary>Whether the Azerbaijani catalog has this key.</summary>
    public static bool Has(string key) => Catalog(DefaultLanguage).ContainsKey(key);

    /// <summary>The message in <paramref name="language"/>, or in <see cref="Current"/>.</summary>
    public static string Write(UserMessage message, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.IsLiteral)
        {
            return message.Key;
        }

        var lang = Normalise(language) ?? Current;

        if (!Catalog(lang).TryGetValue(message.Key, out var template)
            && !Catalog(DefaultLanguage).TryGetValue(message.Key, out template))
        {
            // A key with no text is a bug the parity test catches; the key is
            // still better than nothing for whoever reports it.
            return message.Key;
        }

        return Fill(template, message.Arguments, lang);
    }

    /// <summary>A key with its placeholder values, written out.</summary>
    public static string Write(string key, params (string Name, object? Value)[] arguments)
        => Write(UserMessage.Of(key, arguments));

    /// <summary>
    /// A stored message (<see cref="UserMessage.Encode"/> or
    /// <see cref="UserMessage.EncodeAll"/>, or older plain text), written out.
    /// </summary>
    public static string? WriteStored(string? stored, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        if (!stored.Contains('\n', StringComparison.Ordinal))
        {
            return Write(UserMessage.Decode(stored), language);
        }

        // A list reads as one line, its parts joined by commas.
        var parts = stored
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Write(UserMessage.Decode(part), language))
            .ToList();

        return parts.Count == 1 ? parts[0] : string.Join(", ", parts);
    }

    private static string? Normalise(string? language) => Supported(language);

    private static string Fill(string template, IReadOnlyDictionary<string, string> arguments, string language)
    {
        if (arguments.Count == 0 || template.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);

            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);

            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            var name = template[(open + 1)..close];

            // {type:fiscal.receiptType}: the value is a code ("TopUp") written
            // out through the catalog ("fiscal.receiptType.topUp"), so a stored
            // message reads in the reader's words, not the enum's.
            var colon = name.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0 && arguments.TryGetValue(name[..colon], out var code))
            {
                var key = $"{name[(colon + 1)..]}.{(code.Length > 0 ? char.ToLowerInvariant(code[0]) + code[1..] : code)}";
                builder.Append(Has(key) ? Write(new UserMessage(key), language) : code);
                index = close + 1;
                continue;
            }

            builder.Append(!arguments.TryGetValue(name, out var value)
                ? template[open..(close + 1)]
                // An argument named ...Message is itself a stored message: the
                // provider's problem inside "the connection test failed: ...".
                : name.EndsWith("Message", StringComparison.Ordinal)
                    ? WriteStored(value, language) ?? string.Empty
                    : Localise(value, language));

            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A decimal argument in the reader's style: <c>12,5</c> in Azerbaijani and
    /// Russian, <c>12.5</c> in English (docs/DESIGN_SYSTEM.md section 6).
    /// </summary>
    private static string Localise(string value, string language)
    {
        if (language == "en" || value.Length == 0)
        {
            return value;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0 || dot == value.Length - 1)
        {
            return value;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not '.' and not '-')
            {
                return value;
            }
        }

        return string.Concat(value.AsSpan(0, dot), ",", value.AsSpan(dot + 1));
    }

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(Messages).Assembly;
        var catalogs = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var language in Languages)
        {
            var name = $"NeoPrint.Application.Localization.messages.{language}.json";
            using var stream = assembly.GetManifestResourceStream(name)
                               ?? throw new InvalidOperationException($"The {language} message catalog is not embedded ({name}).");

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                          ?? throw new InvalidOperationException($"The {language} message catalog is empty.");

            catalogs[language] = entries.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return catalogs.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
