using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Converter;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>
/// Page previews, drawn by the converter tool (PDFium, encoded as PNG by
/// SkiaSharp) - never in this process (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
public sealed class ToolPageRenderer : IPageRenderer
{
    private const string Input = "input.pdf";
    private const string Output = "output.png";

    private readonly ConverterTools _tools;

    public ToolPageRenderer(ConverterTools tools) => _tools = tools;

    /// <summary>
    /// Always: whether the converter is running is found out by asking it, and
    /// a converter that is down is reported as such, not as "no previews here".
    /// </summary>
    public bool IsAvailable => true;

    public async Task<byte[]> RenderPngAsync(
        Stream pdf, int pageIndex, int width, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        using var workspace = await _tools.OpenWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        await workspace.WriteAsync(Input, pdf, cancellationToken).ConfigureAwait(false);

        var response = await _tools.RunAsync(
                workspace.Directory,
                ToolContract.PdfRender,
                new ToolRequest(Input, Output, PageIndex: pageIndex, Width: width),
                ConverterTools.InteractiveTimeout,
                failFast: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new PageRenderException(ConverterTools.Describe(response, "preview.failed"));
        }

        return await File.ReadAllBytesAsync(workspace.PathOf(Output), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// File edits, carried out by the converter tool: PDF page operations with
/// PDFsharp, image operations with SkiaSharp (plan section 2.2) - never in
/// this process (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
public sealed class ToolFileEditor : IFileEditor
{
    private readonly ConverterTools _tools;

    public ToolFileEditor(ConverterTools tools) => _tools = tools;

    public async Task EditPagesAsync(
        Stream pdf, IReadOnlyList<PageEdit> pages, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(output);

        const string input = "input.pdf";
        const string result = "edited.pdf";

        using var workspace = await _tools.OpenWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        await workspace.WriteAsync(input, pdf, cancellationToken).ConfigureAwait(false);

        var response = await _tools.RunAsync(
                workspace.Directory,
                ToolContract.EditPages,
                new ToolRequest(input, result, Pages: [.. pages.Select(page => new ToolPageEdit(page.Page, page.Rotate))]),
                ConverterTools.InteractiveTimeout,
                failFast: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            throw new FileEditException(ConverterTools.Describe(response, "edit.pagesFailed"));
        }

        await CopyAsync(workspace.PathOf(result), output, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EditImageAsync(
        Stream image, ImageEdit edit, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(output);

        // The tool reads the format from the content, not from a name.
        const string input = "input.image";
        const string result = "edited.image";

        using var workspace = await _tools.OpenWorkspaceAsync(cancellationToken).ConfigureAwait(false);

        await workspace.WriteAsync(input, image, cancellationToken).ConfigureAwait(false);

        var crop = edit.Crop is { } area ? new ToolImageCrop(area.X, area.Y, area.Width, area.Height) : null;

        var response = await _tools.RunAsync(
                workspace.Directory,
                ToolContract.EditImage,
                new ToolRequest(
                    input, result, Image: new ToolImageEdit(edit.Rotate, crop, edit.Grayscale, edit.Brightness, edit.Contrast)),
                ConverterTools.InteractiveTimeout,
                failFast: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (response is not { Ok: true, Extension: { Length: > 0 } extension })
        {
            throw new FileEditException(ConverterTools.Describe(response, "edit.imageUnopenable"));
        }

        await CopyAsync(workspace.PathOf(result), output, cancellationToken).ConfigureAwait(false);

        return extension;
    }

    private static async Task CopyAsync(string path, Stream output, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

        await file.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
