namespace NeoPrint.Application.Customers;

/// <summary>
/// A file's name as the admin panel shows it (plan section 8A.3: "fayl
/// metadata-sı (məzmun yox)"): enough to tell two files apart with the
/// customer on the phone, not enough to read what a document is. A name can
/// say as much as the content - "Əli Məmmədov diaqnoz.pdf".
/// </summary>
public static class FileNameMask
{
    /// <summary>What stands for the hidden part.</summary>
    public const string Hidden = "•••";

    /// <summary>
    /// The first two letters of the name, the rest hidden, the extension kept:
    /// <c>He•••.pdf</c>. A personal document's name is hidden whole.
    /// </summary>
    public static string Mask(string? fileName, bool sensitive)
    {
        var name = (fileName ?? string.Empty).Trim();
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        // Whole letters, not halves of one: an emoji is two chars.
        var letters = new System.Globalization.StringInfo(stem);
        var shown = sensitive || letters.LengthInTextElements <= 2 ? string.Empty : letters.SubstringByTextElements(0, 2);

        return shown + Hidden + extension;
    }
}
