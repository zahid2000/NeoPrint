using NeoPrint.Layout;

using PdfSharp.Pdf.IO;

namespace NeoPrint.Converter;

/// <summary>PDF page operations with PDFsharp: delete, reorder, rotate (plan section 2.2).</summary>
internal static class PagesTool
{
    public static ToolResponse Edit(string directory, ToolRequest request)
    {
        var inputPath = ToolFiles.Existing(directory, request.Input);
        var outputPath = ToolFiles.Local(directory, request.Output);
        var pages = request.Pages ?? throw new ToolUsageException("A page edit needs its pages.");

        try
        {
            using var input = PdfReader.Open(inputPath, PdfDocumentOpenMode.Import);
            using var result = new PdfSharp.Pdf.PdfDocument();

            // PDFsharp 6.2 ends the streams it compresses itself without the
            // final block and checksum (T-198); the pages keep their own.
            result.Options.CompressContentStreams = false;

            foreach (var edit in pages)
            {
                if (edit.Page < 1 || edit.Page > input.PageCount)
                {
                    throw new ToolException("files.pageMissing", ("count", input.PageCount), ("page", edit.Page));
                }

                var page = result.AddPage(input.Pages[edit.Page - 1]);

                // Added to whatever rotation the page already had, so a page
                // that was stored sideways turns from where the user saw it.
                page.Rotate = (((page.Rotate + edit.Rotate) % 360) + 360) % 360;
            }

            // Saving closes the document; its page count is asked first.
            var pageCount = result.PageCount;

            result.Save(outputPath);

            return new ToolResponse(true, PageCount: pageCount);
        }
        catch (Exception exception) when (exception is not ToolException)
        {
            Console.Error.WriteLine($"The page edit failed: {exception.GetType().Name}.");
            throw new ToolException("edit.pagesFailed");
        }
    }
}

/// <summary>
/// A page layout drawn as a PDF at real size by <c>NeoPrint.Layout</c>, the
/// renderer the kiosk runs too (plan section 2.11). Its pictures - a scan's
/// photos, a user's images - are decoded here.
/// </summary>
internal static class LayoutTool
{
    public static ToolResponse Render(string directory, ToolRequest request)
    {
        var input = ToolFiles.Existing(directory, request.Input);
        var output = ToolFiles.Local(directory, request.Output);
        var pictures = request.Pictures ?? new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var project = LayoutJson.Parse(File.ReadAllText(input));

            using var file = File.Create(output);

            LayoutRenderer.Render(
                project,
                name => pictures.TryGetValue(name, out var picture)
                    ? File.ReadAllBytes(ToolFiles.Existing(directory, picture))
                    : null,
                file);

            return new ToolResponse(true, PageCount: project.Pages.Count);
        }
        catch (LayoutException exception)
        {
            // A layout problem is already a message key.
            throw new ToolException(exception.Message);
        }
    }
}
