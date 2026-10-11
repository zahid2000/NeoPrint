using NeoPrint.Pdfium;

using SkiaSharp;

namespace NeoPrint.Converter;

/// <summary>
/// Opens a PDF with PDFium: its page count, and one page as a PNG encoded by
/// SkiaSharp (plan section 2.2; the previews of plan section 6).
/// </summary>
internal static class PdfTool
{
    /// <summary>
    /// How many pages the PDF has. Also how a damaged or encrypted PDF is
    /// caught before it reaches a printer.
    /// </summary>
    public static ToolResponse Info(string directory, ToolRequest request)
    {
        using var document = Open(ToolFiles.Existing(directory, request.Input));

        return new ToolResponse(true, PageCount: document.PageCount);
    }

    public static ToolResponse Render(string directory, ToolRequest request)
    {
        var input = ToolFiles.Existing(directory, request.Input);
        var output = ToolFiles.Local(directory, request.Output);

        if (request.Width is < 1 or > ToolContract.MaxRenderWidth)
        {
            throw new ToolUsageException("The width is out of range.");
        }

        try
        {
            using var document = Open(input);

            if (request.PageIndex < 0 || request.PageIndex >= document.PageCount)
            {
                throw new ToolException("preview.pageCount", ("count", document.PageCount));
            }

            var (pixels, width, height, stride) = document.RenderPage(request.PageIndex, request.Width);

            // PDFium writes BGRA, opaque (the page is filled white first).
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);

            using var image = SKImage.FromPixelCopy(info, pixels, stride) ?? throw new ToolException("preview.failed");
            using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ToolException("preview.failed");
            using var file = File.Create(output);

            data.SaveTo(file);

            return new ToolResponse(true, PageCount: document.PageCount);
        }
        catch (PdfiumException exception)
        {
            throw new ToolException(exception.MessageKey);
        }
    }

    private static PdfDocument Open(string path)
    {
        try
        {
            return PdfDocument.Load(File.ReadAllBytes(path));
        }
        catch (PdfiumException exception)
        {
            throw new ToolException(exception.MessageKey);
        }
    }
}
