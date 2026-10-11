namespace NeoPrint.Converter;

/// <summary>
/// What the API and the converter tool say to each other
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// This file is compiled into both sides - the tool, and
/// <c>NeoPrint.Infrastructure</c> as a linked file - so the API does not
/// reference the tool's assembly, and with it PDFium, SkiaSharp and PDFsharp.
/// The two talk through files in the operation's own directory: the request,
/// the response, and the documents named in them.
/// </remarks>
internal static class ToolContract
{
    /// <summary>The name the sandbox and the worker's allowlist know the tool by.</summary>
    public const string Program = "neoprint-tool";

    /// <summary>The tool's assembly, next to the API's.</summary>
    public const string AssemblyFile = "NeoPrint.Converter.dll";

    public const string RequestFile = "tool-request.json";
    public const string ResponseFile = "tool-response.json";

    /// <summary>The page count of a PDF.</summary>
    public const string PdfInfo = "pdf-info";

    /// <summary>One page of a PDF as a PNG.</summary>
    public const string PdfRender = "pdf-render";

    /// <summary>A raster image as a one-page PDF.</summary>
    public const string ImageToPdf = "image-to-pdf";

    /// <summary>A PDF made of the listed pages of another.</summary>
    public const string EditPages = "edit-pages";

    /// <summary>A cropped, turned or adjusted image.</summary>
    public const string EditImage = "edit-image";

    /// <summary>A page layout drawn as a PDF.</summary>
    public const string LayoutRender = "layout-render";

    /// <summary>
    /// The largest image that is decoded, in pixels. 100 megapixels is far
    /// above any phone camera and well below what a crafted file can claim: a
    /// PNG header can promise 4 GB of pixels in a few kilobytes, so the size
    /// is read from the header before anything is decoded.
    /// </summary>
    public const long MaxImagePixels = 100_000_000;

    /// <summary>The widest a page is drawn, in pixels: twice A4 at 300 dpi.</summary>
    public const int MaxRenderWidth = 4960;
}

/// <summary>One operation. Every file is a bare name inside the operation's directory.</summary>
/// <param name="Input">The document to read; for a layout, its JSON.</param>
/// <param name="Output">The file to write.</param>
/// <param name="PageIndex">Zero-based, for <see cref="ToolContract.PdfRender"/>.</param>
/// <param name="Width">Pixels, for <see cref="ToolContract.PdfRender"/>.</param>
/// <param name="Pages">The pages of the result, for <see cref="ToolContract.EditPages"/>.</param>
/// <param name="Image">What to do to the image, for <see cref="ToolContract.EditImage"/>.</param>
/// <param name="Pictures">A layout's picture names and the files that hold them.</param>
internal sealed record ToolRequest(
    string? Input = null,
    string? Output = null,
    int PageIndex = 0,
    int Width = 0,
    IReadOnlyList<ToolPageEdit>? Pages = null,
    ToolImageEdit? Image = null,
    IReadOnlyDictionary<string, string>? Pictures = null);

/// <param name="Page">One-based page of the input.</param>
/// <param name="Rotate">Clockwise degrees added to the page's own rotation.</param>
internal sealed record ToolPageEdit(int Page, int Rotate);

/// <summary>Applied in this order: crop, rotate, grey, light.</summary>
internal sealed record ToolImageEdit(int Rotate, ToolImageCrop? Crop, bool Grayscale, int Brightness, int Contrast);

/// <summary>Fractions of the image: 0,0 is the top left, 1,1 the bottom right.</summary>
internal sealed record ToolImageCrop(double X, double Y, double Width, double Height);

/// <summary>What the tool answers.</summary>
/// <param name="Error">
/// A message key the user reads in their own language, when the document is
/// the problem. Null with <paramref name="Ok"/> false means the tool failed
/// for a reason of its own, and the caller says so in its own words.
/// </param>
/// <param name="Arguments">The values the message names.</param>
/// <param name="PageCount">The input's pages, where the operation opened a PDF.</param>
/// <param name="Extension">The format written, for <see cref="ToolContract.EditImage"/>.</param>
internal sealed record ToolResponse(
    bool Ok,
    string? Error = null,
    IReadOnlyDictionary<string, string>? Arguments = null,
    int? PageCount = null,
    string? Extension = null);
