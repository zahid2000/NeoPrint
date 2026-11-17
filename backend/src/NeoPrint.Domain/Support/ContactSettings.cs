using System.Text.RegularExpressions;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Support;

/// <summary>How a customer reaches the company (T-438), in the order the app lists them.</summary>
public enum ContactKind
{
    /// <summary>The hotline: a number to call, short ones (<c>*1234</c>) too.</summary>
    Phone = 0,

    /// <summary>A number or a wa.me link; the app opens WhatsApp.</summary>
    WhatsApp = 1,

    /// <summary>A username, a number or a t.me link; the app opens Telegram.</summary>
    Telegram = 2,

    Email = 3,
}

/// <summary>One way to reach the company: what the admin wrote and whether the app shows it.</summary>
public sealed class ContactChannel
{
    public ContactKind Kind { get; set; }

    /// <summary>As written in the panel; what the app shows beside the icon.</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

/// <summary>
/// The "Əlaqə" section of the app (T-438): the hotline, WhatsApp, Telegram and
/// email, each set and switched on or off in the panel. One document.
/// </summary>
/// <remarks>
/// Every link the app opens is made here from what the admin wrote, and only
/// to the four known places (<c>tel:</c>, wa.me, t.me, <c>mailto:</c>): a
/// value that would make any other link is refused, so the panel cannot be
/// used to send customers somewhere else.
/// </remarks>
public sealed partial class ContactSettings : Entity
{
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-000000000438");

    public const int MaxValueLength = 120;

    public static readonly IReadOnlyList<ContactKind> Kinds = Enum.GetValues<ContactKind>();

    public List<ContactChannel> Channels { get; set; } = [];

    public DateTime UpdatedAt { get; set; }

    /// <summary>The channel of this kind, as stored; an empty, inactive one when none was.</summary>
    public ContactChannel Channel(ContactKind kind)
        => Channels.Find(channel => channel.Kind == kind) ?? new ContactChannel { Kind = kind };

    /// <summary>Puts these in place of the ones there, one per kind.</summary>
    /// <returns>(kind, message key) for each that cannot be kept; nothing is changed then.</returns>
    public IReadOnlyList<(ContactKind Kind, string Message)> Set(IEnumerable<ContactChannel> channels, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var given = channels.ToList();
        var wrong = new List<(ContactKind, string)>();
        var kept = new List<ContactChannel>();

        foreach (var kind in Kinds)
        {
            var same = given.Where(channel => channel.Kind == kind).ToList();
            if (same.Count > 1)
            {
                wrong.Add((kind, "contact.duplicate"));
                continue;
            }

            var channel = same.FirstOrDefault() ?? new ContactChannel { Kind = kind };
            var value = channel.Value?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                if (channel.IsActive)
                {
                    wrong.Add((kind, "contact.valueRequired"));
                }

                kept.Add(new ContactChannel { Kind = kind, Value = string.Empty, IsActive = false });
                continue;
            }

            if (value.Length > MaxValueLength || Link(kind, value) is null)
            {
                wrong.Add((kind, InvalidKey(kind)));
                continue;
            }

            kept.Add(new ContactChannel { Kind = kind, Value = value, IsActive = channel.IsActive });
        }

        if (given.Any(channel => !Enum.IsDefined(channel.Kind)))
        {
            wrong.Add((ContactKind.Phone, "contact.unknownKind"));
        }

        if (wrong.Count > 0)
        {
            return wrong;
        }

        Channels = kept;
        UpdatedAt = utcNow;
        return [];
    }

    /// <summary>The link the app opens for this value; null when it makes none of the four.</summary>
    public static string? Link(ContactKind kind, string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return kind switch
        {
            ContactKind.Phone => PhoneNumber().IsMatch(text) && text.Count(char.IsAsciiDigit) >= 3
                ? "tel:" + Uri.EscapeDataString(Dialled(text))
                : null,
            ContactKind.WhatsApp => HttpsLinkTo(text, "wa.me", "api.whatsapp.com", "chat.whatsapp.com")
                ?? (InternationalNumber().IsMatch(Dialled(text)) ? "https://wa.me/" + Dialled(text).TrimStart('+') : null),
            ContactKind.Telegram => HttpsLinkTo(text, "t.me", "telegram.me")
                ?? (TelegramUser().Match(text) is { Success: true } user
                    ? "https://t.me/" + user.Groups["name"].Value
                    : InternationalNumber().IsMatch(Dialled(text)) ? "https://t.me/+" + Dialled(text).TrimStart('+') : null),
            ContactKind.Email => Email().IsMatch(text) ? "mailto:" + text : null,
            _ => null,
        };
    }

    /// <summary>The message for a value of this kind that makes no link - spelled out, so the catalogs can be checked.</summary>
    private static string InvalidKey(ContactKind kind) => kind switch
    {
        ContactKind.Phone => "contact.invalidPhone",
        ContactKind.WhatsApp => "contact.invalidWhatsApp",
        ContactKind.Telegram => "contact.invalidTelegram",
        _ => "contact.invalidEmail",
    };

    /// <summary>A number as it is dialled: no spaces, dashes or brackets.</summary>
    private static string Dialled(string text)
        => new([.. text.Where(c => c is not (' ' or '-' or '(' or ')'))]);

    /// <summary>The value itself, when it is an https link to one of these hosts.</summary>
    private static string? HttpsLinkTo(string text, params string[] hosts)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
           && string.IsNullOrEmpty(uri.UserInfo)
           && uri.IsDefaultPort
            ? uri.AbsoluteUri
            : null;

    [GeneratedRegex(@"^\+?[0-9 ()\-*#]{3,32}$")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"^\+?[0-9]{7,15}$")]
    private static partial Regex InternationalNumber();

    [GeneratedRegex(@"^@?(?<name>[A-Za-z][A-Za-z0-9_]{4,31})$")]
    private static partial Regex TelegramUser();

    [GeneratedRegex(@"^[^@\s<>""]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)+$")]
    private static partial Regex Email();
}
