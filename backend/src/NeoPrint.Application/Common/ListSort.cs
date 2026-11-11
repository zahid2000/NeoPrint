namespace NeoPrint.Application.Common;

/// <summary>
/// How an admin list the server pages is ordered (T-424): a column named by
/// the page, <c>sort=total</c> for smallest first, <c>sort=-total</c> for
/// largest first. Without one a list keeps its own order (newest first).
/// </summary>
/// <param name="Field">One of the names the list allows, as the list spells it.</param>
public sealed record ListSort(string Field, bool Descending)
{
    /// <summary>
    /// Reads <paramref name="value"/> against the columns a list can be ordered
    /// by; null for none. A name the list does not know is a mistake the
    /// caller hears about, not a silent "newest first".
    /// </summary>
    public static ListSort? Parse(string? value, params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var descending = text.StartsWith('-');
        var name = descending ? text[1..] : text;

        var field = Array.Find(fields, known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ValidationException(
                [new ValidationError("sort", "common.sortInvalid", ("fields", string.Join(", ", fields)))]);

        return new ListSort(field, descending);
    }
}
