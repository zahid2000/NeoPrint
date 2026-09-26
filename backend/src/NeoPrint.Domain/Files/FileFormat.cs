namespace NeoPrint.Domain.Files;

/// <summary>How a file becomes a PDF (plan section 2.2).</summary>
public enum ConversionRoute
{
    /// <summary>Already a PDF. Used as it is.</summary>
    None = 0,

    /// <summary>Office documents, spreadsheets, presentations, text, HTML.</summary>
    LibreOffice = 1,

    /// <summary>PostScript, PCL and XPS, through the Ghostscript family.</summary>
    Ghostscript = 2,

    /// <summary>Raster images, wrapped into a PDF page with SkiaSharp.</summary>
    Image = 3,
}

/// <summary>The groups the plan's format table is organised by.</summary>
public enum FormatGroup
{
    Pdf = 0,
    TextDocument = 1,
    Spreadsheet = 2,
    Presentation = 3,
    Image = 4,

    /// <summary>Printer page description languages (IPP/PWG).</summary>
    PageDescription = 5,
}

/// <summary>
/// One accepted file format.
/// </summary>
/// <param name="Extension">Canonical extension, lower case, without a dot.</param>
/// <param name="MediaType">What the API reports as the content type.</param>
/// <param name="Group">Which row of the plan's table it comes from.</param>
/// <param name="Route">How it is turned into a PDF.</param>
/// <param name="PrinterNative">
/// Whether a printer may be sent the original file when it advertises the
/// format and nothing was edited (plan section 2.2).
/// </param>
public sealed record FileFormat(
    string Extension,
    string MediaType,
    FormatGroup Group,
    ConversionRoute Route,
    bool PrinterNative);

/// <summary>
/// The MVP format list (plan section 2.2).
/// </summary>
/// <remarks>
/// The catalogue is fixed in code; which entries are <em>enabled</em> is the
/// administrator's decision, held in <c>Files.AllowedFormats</c>. Those are
/// different things: turning a format off is configuration, adding one means
/// a converter that has to exist.
/// </remarks>
public static class FileFormats
{
    /// <summary>Every format the MVP knows how to handle.</summary>
    public static readonly IReadOnlyList<FileFormat> All =
    [
        new("pdf", "application/pdf", FormatGroup.Pdf, ConversionRoute.None, PrinterNative: true),

        // Text documents - LibreOffice.
        new("doc", "application/msword", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("docm", "application/vnd.ms-word.document.macroEnabled.12", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("dot", "application/msword", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("dotx", "application/vnd.openxmlformats-officedocument.wordprocessingml.template", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("odt", "application/vnd.oasis.opendocument.text", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("ott", "application/vnd.oasis.opendocument.text-template", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("rtf", "application/rtf", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("wps", "application/vnd.ms-works", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("txt", "text/plain", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("html", "text/html", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("htm", "text/html", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),
        new("xml", "application/xml", FormatGroup.TextDocument, ConversionRoute.LibreOffice, false),

        // Spreadsheets.
        new("xls", "application/vnd.ms-excel", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),
        new("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),
        new("xlsm", "application/vnd.ms-excel.sheet.macroEnabled.12", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),
        new("xltx", "application/vnd.openxmlformats-officedocument.spreadsheetml.template", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),
        new("ods", "application/vnd.oasis.opendocument.spreadsheet", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),
        new("csv", "text/csv", FormatGroup.Spreadsheet, ConversionRoute.LibreOffice, false),

        // Presentations.
        new("ppt", "application/vnd.ms-powerpoint", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),
        new("pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),
        new("pps", "application/vnd.ms-powerpoint", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),
        new("ppsx", "application/vnd.openxmlformats-officedocument.presentationml.slideshow", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),
        new("potx", "application/vnd.openxmlformats-officedocument.presentationml.template", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),
        new("odp", "application/vnd.oasis.opendocument.presentation", FormatGroup.Presentation, ConversionRoute.LibreOffice, false),

        // Images. JPEG is the one a printer may take directly (PWG 5100).
        new("jpg", "image/jpeg", FormatGroup.Image, ConversionRoute.Image, PrinterNative: true),
        new("jpeg", "image/jpeg", FormatGroup.Image, ConversionRoute.Image, PrinterNative: true),
        new("png", "image/png", FormatGroup.Image, ConversionRoute.Image, false),
        new("bmp", "image/bmp", FormatGroup.Image, ConversionRoute.Image, false),
        new("gif", "image/gif", FormatGroup.Image, ConversionRoute.Image, false),
        new("webp", "image/webp", FormatGroup.Image, ConversionRoute.Image, false),

        // Printer page description languages.
        new("ps", "application/postscript", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
        new("eps", "application/postscript", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
        new("pcl", "application/vnd.hp-pcl", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
        new("prn", "application/octet-stream", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
        new("xps", "application/vnd.ms-xpsdocument", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
        new("oxps", "application/oxps", FormatGroup.PageDescription, ConversionRoute.Ghostscript, PrinterNative: true),
    ];

    private static readonly Dictionary<string, FileFormat> ByExtension =
        All.GroupBy(f => f.Extension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks a format up by extension, with or without a leading dot.</summary>
    public static FileFormat? Find(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return ByExtension.GetValueOrDefault(extension.TrimStart('.').Trim());
    }

    /// <summary>Every extension, for the admin panel's format page.</summary>
    public static IReadOnlyList<string> Extensions => [.. ByExtension.Keys.Order(StringComparer.Ordinal)];
}
