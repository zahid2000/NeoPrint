using System.Text;

namespace NeoPrint.Domain.Files;

/// <summary>What the bytes of a file say it is.</summary>
/// <param name="Format">The format recognised, or null.</param>
/// <param name="Reason">Why nothing was recognised, when that is the case.</param>
public sealed record SignatureResult(FileFormat? Format, string? Reason)
{
    public bool Recognised => Format is not null;

    public static SignatureResult Of(FileFormat format) => new(format, null);

    public static SignatureResult Unrecognised(string reason) => new(null, reason);
}

/// <summary>
/// Identifies a file by its content, not by its name (plan section 2.2).
/// </summary>
/// <remarks>
/// <para>
/// An extension is a claim the uploader makes. Trusting it means a
/// <c>.jpg</c> that is really a PostScript program reaches Ghostscript, and
/// the format the administrator disabled is the one that gets processed. So
/// the extension only narrows the search: what the bytes say decides.
/// </para>
/// <para>
/// The container formats need a second look. OOXML and OpenDocument are both
/// ZIP archives, so the magic bytes say "ZIP" and nothing more; which
/// document it is comes from what is inside. Reading the whole archive here
/// would mean parsing untrusted input in the API, which is what the
/// conversion sandbox exists to avoid - so the archive is examined only far
/// enough to read the entry names, and the declared extension breaks the tie
/// between, say, .docx and .xlsx <em>within</em> the family it has proven to
/// belong to.
/// </para>
/// </remarks>
public static class ContentSignature
{
    /// <summary>Bytes to read from the front of a file. Enough for every signature below.</summary>
    public const int HeaderLength = 512;

    /// <summary>
    /// Identifies a file from its first bytes and the name it was uploaded
    /// under.
    /// </summary>
    /// <param name="header">
    /// The first <see cref="HeaderLength"/> bytes, or the whole file when it
    /// is shorter.
    /// </param>
    /// <param name="fileName">The name the uploader gave, used only to break ties.</param>
    public static SignatureResult Identify(ReadOnlySpan<byte> header, string? fileName)
    {
        if (header.Length == 0)
        {
            return SignatureResult.Unrecognised("Fayl boşdur.");
        }

        var declared = FileFormats.Find(Path.GetExtension(fileName));

        // --- Unambiguous signatures ------------------------------------
        if (StartsWith(header, "%PDF-"u8))
        {
            return Recognise("pdf");
        }

        if (StartsWith(header, "%!PS"u8))
        {
            // EPS is PostScript with a bounding box comment; both go to
            // Ghostscript, so the declared extension decides which name is
            // reported.
            return Recognise(declared?.Extension is "eps" ? "eps" : "ps");
        }

        if (StartsWith(header, [0xFF, 0xD8, 0xFF]))
        {
            return Recognise(declared?.Extension is "jpeg" ? "jpeg" : "jpg");
        }

        if (StartsWith(header, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Recognise("png");
        }

        if (StartsWith(header, "BM"u8))
        {
            return Recognise("bmp");
        }

        if (StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8))
        {
            return Recognise("gif");
        }

        // RIFF....WEBP
        if (StartsWith(header, "RIFF"u8) && header.Length >= 12 && header[8..12].SequenceEqual("WEBP"u8))
        {
            return Recognise("webp");
        }

        if (StartsWith(header, [0x1B, (byte)'%', (byte)'-', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'X'])
            || StartsWith(header, [0x1B, (byte)'E']))
        {
            // PJL or a PCL reset: both are what a print driver emits.
            return Recognise("pcl");
        }

        if (StartsWith(header, "{\\rtf"u8))
        {
            return Recognise("rtf");
        }

        // --- Containers -------------------------------------------------

        // ZIP: OOXML, OpenDocument, and XPS - which is also a ZIP.
        if (StartsWith(header, [(byte)'P', (byte)'K', 0x03, 0x04])
            || StartsWith(header, [(byte)'P', (byte)'K', 0x05, 0x06])
            || StartsWith(header, [(byte)'P', (byte)'K', 0x07, 0x08]))
        {
            return FromZip(header, declared);
        }

        // OLE2 compound file: the pre-2007 Office formats, which cannot be
        // told apart from the first bytes alone.
        if (StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return declared is { Extension: "doc" or "xls" or "ppt" or "pps" or "dot" or "wps" }
                ? SignatureResult.Of(declared)
                : SignatureResult.Unrecognised(
                    "Fayl köhnə Office formatındadır, lakin uzantısı hansı növ olduğunu göstərmir.");
        }

        // --- Text ---------------------------------------------------------
        // Nothing binary matched. TXT, CSV, HTML and XML have no signature, so
        // the rule is: it has to *look* like text, and then the extension says
        // which of them it is. A binary file cannot slip through as .txt this
        // way, which is the property that matters.
        if (declared is { Group: FormatGroup.TextDocument or FormatGroup.Spreadsheet }
            && declared.Extension is "txt" or "csv" or "html" or "htm" or "xml"
            && LooksLikeText(header))
        {
            return SignatureResult.Of(declared);
        }

        return SignatureResult.Unrecognised(
            "Faylın məzmunu tanınmadı. Dəstəklənən formatların siyahısına baxın.");
    }

    /// <summary>
    /// Narrows a ZIP container down using the entry names in its first bytes.
    /// </summary>
    /// <remarks>
    /// Only the local file header that the archive starts with is read, which
    /// is where the first entry's name sits. That is enough to separate the
    /// families without decompressing anything - but which part comes first
    /// depends on the program that wrote the file, not on the format:
    /// Microsoft Office starts OOXML with <c>[Content_Types].xml</c>,
    /// LibreOffice with <c>_rels/.rels</c>, other tools with a part of the
    /// document itself; Ghostscript starts XPS with
    /// <c>FixedDocumentSequence.fdseq</c>. OpenDocument always starts with
    /// <c>mimetype</c>, as its specification requires.
    /// </remarks>
    private static SignatureResult FromZip(ReadOnlySpan<byte> header, FileFormat? declared)
    {
        var text = Encoding.ASCII.GetString(header);

        var family = text.Contains("mimetype", StringComparison.Ordinal)
                     || text.Contains("opendocument", StringComparison.Ordinal)
            ? FamilyOpenDocument
            : OoxmlFirstParts.Any(part => text.Contains(part, StringComparison.Ordinal))
                ? FamilyOoxml
                : null;

        if (family is null)
        {
            return SignatureResult.Unrecognised(
                "Fayl ZIP arxividir, lakin dəstəklənən sənəd formatlarından birinə uyğun gəlmir.");
        }

        // Within the family the extension decides: .docx and .xlsx are the
        // same container with different parts, and telling them apart would
        // mean unpacking untrusted input here rather than in the sandbox.
        if (declared is not null && family.Contains(declared.Extension, StringComparer.Ordinal))
        {
            return SignatureResult.Of(declared);
        }

        return SignatureResult.Unrecognised(
            "Faylın uzantısı onun daxili quruluşu ilə uyğun gəlmir.");
    }

    /// <summary>
    /// Names an OOXML or XPS package can start with: the package parts, then
    /// the folders of each document type.
    /// </summary>
    private static readonly string[] OoxmlFirstParts =
    [
        "[Content_Types].xml",
        "_rels/.rels",
        "docProps/",
        "word/",
        "xl/",
        "ppt/",
        "FixedDocumentSequence.fdseq",
        "FixedDocSeq.fdseq",
        "Documents/",
    ];

    private static readonly string[] FamilyOoxml =
        ["docx", "docm", "dotx", "xlsx", "xlsm", "xltx", "pptx", "ppsx", "potx", "xps", "oxps"];

    private static readonly string[] FamilyOpenDocument = ["odt", "ott", "ods", "odp"];

    /// <summary>
    /// Whether the bytes are plausibly text.
    /// </summary>
    /// <remarks>
    /// A NUL byte in the first block is what separates a text file from a
    /// binary one in practice; the other control characters that do appear in
    /// text are allowed through.
    /// </remarks>
    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        foreach (var b in header)
        {
            if (b == 0)
            {
                return false;
            }

            // C0 controls other than tab, newline, carriage return and form feed.
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
            {
                return false;
            }
        }

        return true;
    }

    private static SignatureResult Recognise(string extension)
        => FileFormats.Find(extension) is { } format
            ? SignatureResult.Of(format)
            : SignatureResult.Unrecognised("Format kataloqda yoxdur.");

    private static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> prefix)
        => header.Length >= prefix.Length && header[..prefix.Length].SequenceEqual(prefix);
}
