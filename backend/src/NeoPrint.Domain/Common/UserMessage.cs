using System.Globalization;
using System.Text;

namespace NeoPrint.Domain.Common;

/// <summary>
/// Something to tell a person, as a key into the message catalogs plus the
/// values that go into it (plan section 2.1: az, en, ru).
/// </summary>
/// <remarks>
/// <para>
/// The text itself is chosen where the reader is known - the API, by the
/// request's language; an email, by the user's - so the same rule broken in
/// the same way reads in Azerbaijani on one phone and in Russian on another.
/// The domain only says which message it is.
/// </para>
/// <para>
/// A message that is kept (why a file failed to convert, why a storage job
/// stopped) is stored as <see cref="Encode"/> and read back with
/// <see cref="Decode"/>, so it is shown in the reader's language on the day
/// it is read rather than the writer's on the day it was written. Text stored
/// before the catalogs existed decodes as itself.
/// </para>
/// <para>
/// The key doubles as the exception message, so a log line carries the key and
/// never the arguments - which may be an email address or a file name.
/// </para>
/// </remarks>
public sealed record UserMessage
{
    private static readonly IReadOnlyDictionary<string, string> NoArguments =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public UserMessage(string key, IReadOnlyDictionary<string, string>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Arguments = arguments ?? NoArguments;
    }

    /// <summary>The catalog key, <c>area.what</c> - for example <c>files.notReady</c>.</summary>
    public string Key { get; }

    /// <summary>The values for the <c>{name}</c> placeholders, already written out.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; }

    /// <summary>
    /// Stored text that is not a key at all - written before the catalogs, or
    /// by a provider - shown as it is.
    /// </summary>
    public bool IsLiteral => !LooksLikeKey(Key);

    /// <summary>A message with its placeholder values; numbers are written invariantly.</summary>
    public static UserMessage Of(string key, params (string Name, object? Value)[] arguments)
    {
        if (arguments.Length == 0)
        {
            return new UserMessage(key);
        }

        var values = new Dictionary<string, string>(arguments.Length, StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            values[name] = value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        return new UserMessage(key, values);
    }

    /// <summary>The form kept in a document: <c>key</c> or <c>key?name=value&amp;...</c>.</summary>
    public string Encode()
    {
        if (Arguments.Count == 0)
        {
            return Key;
        }

        var builder = new StringBuilder(Key).Append('?');
        var first = true;

        foreach (var (name, value) in Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Several messages kept as one - a list the reader sees joined, such as
    /// the changes one edit made. One encoded message per line.
    /// </summary>
    public static string EncodeAll(IEnumerable<UserMessage> messages)
        => string.Join('\n', messages.Select(message => message.Encode()));

    /// <summary>Reads what <see cref="Encode"/> wrote; any other text comes back as a literal.</summary>
    public static UserMessage Decode(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);

        var query = stored.IndexOf('?', StringComparison.Ordinal);
        var key = query < 0 ? stored : stored[..query];

        if (!LooksLikeKey(key))
        {
            return new UserMessage(stored);
        }

        if (query < 0)
        {
            return new UserMessage(key);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in stored[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0)
            {
                values[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }

        return new UserMessage(key, values);
    }

    /// <summary>
    /// Keys are lower-camel segments joined by dots, at least two of them:
    /// <c>files.notReady</c>. A sentence never looks like that.
    /// </summary>
    public static bool LooksLikeKey(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 120)
        {
            return false;
        }

        var segments = 0;
        var segmentStart = true;

        foreach (var character in value)
        {
            if (character == '.')
            {
                if (segmentStart)
                {
                    return false;
                }

                segmentStart = true;
                continue;
            }

            if (segmentStart)
            {
                if (character is < 'a' or > 'z')
                {
                    return false;
                }

                segments++;
                segmentStart = false;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return !segmentStart && segments >= 2;
    }

    public override string ToString() => Encode();
}
