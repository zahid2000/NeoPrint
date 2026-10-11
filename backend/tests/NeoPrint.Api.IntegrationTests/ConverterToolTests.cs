using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Converter;
using NeoPrint.Infrastructure.Conversion;
using NeoPrint.Pdfium;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> for a test that has the converter tool open a
/// PDF: skipped where the PDFium native library is not installed. Unlike
/// <see cref="PdfiumFactAttribute"/> it needs no Docker.
/// </summary>
public sealed class ToolPdfiumFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() => PdfiumLibrary.IsAvailable, isThreadSafe: true);

    public ToolPdfiumFactAttribute()
    {
        if (!Available.Value)
        {
            Skip = "The PDFium native library is not available on this machine. CI and the backend image both have it.";
        }
    }
}

/// <summary>
/// The converter tool: the separate process that is the only thing in the
/// backend to open a user's PDF or image (plan step 7, T-051;
/// docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// These run the real tool as a real child process - the way the converter
/// container runs it, minus the container - through the same classes the API
/// uses. No Docker is needed.
/// </remarks>
public sealed class ConverterToolTests : IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private readonly string _root;
    private readonly ConverterTools _tools;

    public ConverterToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"neoprint-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _tools = Tools(new SandboxedProcess(NullLogger<SandboxedProcess>.Instance));
    }

    public void Dispose()
    {
        _tools.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private ConverterTools Tools(IProcessSandbox sandbox) => new(
        sandbox,
        Options.Create(new ConversionOptions { WorkRoot = _root }),
        NullLogger<ConverterTools>.Instance);

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private string Workspace(string fileName, byte[] content)
    {
        var directory = Path.Combine(_root, $"neoprint-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), content);
        return directory;
    }

    // -----------------------------------------------------------------------
    // PDFs
    // -----------------------------------------------------------------------

    [ToolPdfiumFact]
    public async Task Pages_are_counted_by_the_tool()
    {
        var directory = Workspace("input.pdf", ConversionTests.OnePagePdf(pages: 3));

        var (pageCount, failure) = await _tools.CountPagesAsync(directory, "input.pdf", CancellationToken.None);

        failure.Should().BeNull();
        pageCount.Should().Be(3);
    }

    [ToolPdfiumFact]
    public async Task A_pdf_that_cannot_be_read_is_reported_in_words_for_its_owner()
    {
        var directory = Workspace("input.pdf", "%PDF-1.4\nthis is not a document"u8.ToArray());

        var (_, failure) = await _tools.CountPagesAsync(directory, "input.pdf", CancellationToken.None);

        failure.Should().NotBeNull();
        failure!.Key.Should().StartWith("pdf.", "PDFium's refusal is worded for the person who uploaded the file");
    }

    [ToolPdfiumFact]
    public async Task A_page_is_drawn_as_a_png()
    {
        var renderer = new ToolPageRenderer(_tools);
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf(pages: 2));

        var png = await renderer.RenderPngAsync(pdf, pageIndex: 1, width: 320);

        png.Take(4).Should().Equal(PngSignature);

        using var decoded = SkiaSharp.SKBitmap.Decode(png);
        decoded.Width.Should().Be(320);
        decoded.Height.Should().BeInRange(450, 456, "the page keeps A4's proportions");
    }

    [ToolPdfiumFact]
    public async Task A_page_that_is_not_there_is_refused_with_the_real_page_count()
    {
        var renderer = new ToolPageRenderer(_tools);
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf(pages: 2));

        var render = () => renderer.RenderPngAsync(pdf, pageIndex: 5, width: 320);

        var refused = (await render.Should().ThrowAsync<PageRenderException>()).Which;
        refused.Text.Key.Should().Be("preview.pageCount");
        refused.Text.Arguments["count"].Should().Be("2");
    }

    [ToolPdfiumFact]
    public async Task Pages_are_dropped_reordered_and_turned()
    {
        var editor = new ToolFileEditor(_tools);
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf(pages: 3));
        using var output = new MemoryStream();

        await editor.EditPagesAsync(pdf, [new PageEdit(3, 90), new PageEdit(1)], output);

        var directory = Workspace("input.pdf", output.ToArray());
        var (pageCount, failure) = await _tools.CountPagesAsync(directory, "input.pdf", CancellationToken.None);

        failure.Should().BeNull();
        pageCount.Should().Be(2);
    }

    [Fact]
    public async Task An_edit_that_names_a_missing_page_says_which()
    {
        var editor = new ToolFileEditor(_tools);
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf(pages: 2));
        using var output = new MemoryStream();

        var edit = () => editor.EditPagesAsync(pdf, [new PageEdit(7)], output);

        var refused = (await edit.Should().ThrowAsync<FileEditException>()).Which;
        refused.Text.Key.Should().Be("files.pageMissing");
        refused.Text.Arguments["page"].Should().Be("7");
        refused.Text.Arguments["count"].Should().Be("2");
    }

    // -----------------------------------------------------------------------
    // Images
    // -----------------------------------------------------------------------

    [Fact]
    public async Task An_image_becomes_a_one_page_pdf()
    {
        var converter = new ImageConverter(_tools, NullLogger<ImageConverter>.Instance);
        var directory = Workspace("input.png", Png(40, 20));

        var result = await converter.ConvertAsync(Path.Combine(directory, "input.png"), SandboxLimits.Default(directory));

        result.Succeeded.Should().BeTrue(result.Failure);
        File.ReadAllBytes(result.PdfPath!).Take(5).Should().Equal("%PDF-"u8.ToArray());
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_fails_with_a_message_not_an_exception()
    {
        var converter = new ImageConverter(_tools, NullLogger<ImageConverter>.Instance);
        var directory = Workspace("input.png", "not an image at all"u8.ToArray());

        var result = await converter.ConvertAsync(Path.Combine(directory, "input.png"), SandboxLimits.Default(directory));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be("conversion.imageUnreadable");
    }

    [Fact]
    public async Task An_image_is_turned_in_the_tool_and_keeps_a_format_that_loses_nothing()
    {
        var editor = new ToolFileEditor(_tools);
        using var image = new MemoryStream(Png(40, 20));
        using var output = new MemoryStream();

        var extension = await editor.EditImageAsync(image, new ImageEdit(Rotate: 90, Grayscale: true), output);

        extension.Should().Be("png");

        using var decoded = SkiaSharp.SKBitmap.Decode(output.ToArray());
        (decoded.Width, decoded.Height).Should().Be((20, 40));

        var pixel = decoded.GetPixel(5, 5);
        pixel.Red.Should().Be(pixel.Green).And.Be(pixel.Blue, "the picture was made grey");
    }

    // -----------------------------------------------------------------------
    // Layouts
    // -----------------------------------------------------------------------

    private const string OnePictureLayout =
        """{"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[{"image":"front","xMm":20,"yMm":20,"widthMm":85.6,"heightMm":53.98}]}]}""";

    [Fact]
    public async Task A_layout_is_drawn_as_a_pdf()
    {
        var layouts = new LayoutDocuments(_tools);
        var picture = Png(400, 250);
        using var output = new MemoryStream();

        await layouts.RenderAsync(OnePictureLayout, name => name == "front" ? picture : null, output);

        output.ToArray().Take(5).Should().Equal("%PDF-"u8.ToArray());
    }

    [Fact]
    public async Task A_layout_whose_picture_is_not_a_picture_is_refused_in_words()
    {
        var layouts = new LayoutDocuments(_tools);
        using var output = new MemoryStream();

        var render = () => layouts.RenderAsync(OnePictureLayout, _ => "not a picture"u8.ToArray(), output);

        (await render.Should().ThrowAsync<ValidationException>())
            .Which.Errors.Should().ContainSingle().Which.Message.Key.Should().Be("layout.imageUnreadable");
    }

    [Fact]
    public async Task A_layout_whose_picture_is_missing_never_reaches_the_tool()
    {
        var layouts = new LayoutDocuments(Tools(new Outcome(SandboxOutcome.NotStarted)));
        using var output = new MemoryStream();

        var render = () => layouts.RenderAsync(OnePictureLayout, _ => null, output);

        (await render.Should().ThrowAsync<ValidationException>())
            .Which.Errors.Should().ContainSingle().Which.Message.Key.Should().Be("layout.imageMissing");
    }

    // -----------------------------------------------------------------------
    // The edges
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("../input.pdf")]
    [InlineData("sub/input.pdf")]
    [InlineData("..")]
    public async Task The_tool_opens_nothing_outside_its_own_directory(string name)
    {
        // A file next to the operation's directory, which a path would reach.
        File.WriteAllBytes(Path.Combine(_root, "input.pdf"), ConversionTests.OnePagePdf());
        var directory = Workspace("unrelated.txt", [1]);
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllBytes(Path.Combine(directory, "sub", "input.pdf"), ConversionTests.OnePagePdf());

        var response = await _tools.RunAsync(
            directory, ToolContract.PdfInfo, new ToolRequest(Input: name), TimeSpan.FromSeconds(30), failFast: false, CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.PageCount.Should().BeNull("the tool refused the name instead of opening the file");
    }

    [Fact]
    public async Task An_operation_the_tool_does_not_have_is_not_carried_out()
    {
        var directory = Workspace("input.pdf", ConversionTests.OnePagePdf());

        var response = await _tools.RunAsync(
            directory, "run-anything", new ToolRequest(Input: "input.pdf"), TimeSpan.FromSeconds(30), failFast: false, CancellationToken.None);

        response.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_of_a_document_is_left_behind_when_an_operation_ends()
    {
        var editor = new ToolFileEditor(_tools);

        using (var image = new MemoryStream(Png(40, 20)))
        using (var output = new MemoryStream())
        {
            await editor.EditImageAsync(image, new ImageEdit(Rotate: 180), output);
        }

        using (var broken = new MemoryStream("not an image"u8.ToArray()))
        using (var output = new MemoryStream())
        {
            var edit = () => editor.EditImageAsync(broken, new ImageEdit(Rotate: 180), output);
            await edit.Should().ThrowAsync<FileEditException>();
        }

        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty("each operation deletes its directory, also when it fails");
    }

    /// <summary>A sandbox that ends every run the same way without running anything.</summary>
    private sealed class Outcome(SandboxOutcome outcome) : IProcessSandbox
    {
        public Task<SandboxResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, SandboxLimits limits, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxResult(outcome, outcome == SandboxOutcome.Failed ? 139 : null, string.Empty, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_converter_that_is_not_running_is_not_reported_as_a_bad_file()
    {
        var tools = Tools(new Outcome(SandboxOutcome.NotStarted));
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf());
        using var output = new MemoryStream();

        var render = () => new ToolPageRenderer(tools).RenderPngAsync(pdf, 0, 320);
        var edit = () => new ToolFileEditor(tools).EditPagesAsync(pdf, [new PageEdit(1)], output);

        // 503 with "try again", not 400 with "your file is damaged".
        await render.Should().ThrowAsync<FileProcessingUnavailableException>();
        await edit.Should().ThrowAsync<FileProcessingUnavailableException>();

        // An upload being converted has nobody waiting: it ends as a failed
        // conversion that says the converter did not start.
        var directory = Workspace("input.pdf", ConversionTests.OnePagePdf());
        var (_, failure) = await tools.CountPagesAsync(directory, "input.pdf", CancellationToken.None);
        failure!.Key.Should().Be("conversion.notStarted");
    }

    [Fact]
    public async Task A_tool_that_crashed_on_a_document_fails_that_document_only()
    {
        // Exit 139 is a segmentation fault: the parser died and wrote no answer.
        var tools = Tools(new Outcome(SandboxOutcome.Failed));
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf());

        var render = () => new ToolPageRenderer(tools).RenderPngAsync(pdf, 0, 320);

        (await render.Should().ThrowAsync<PageRenderException>()).Which.Text.Key.Should().Be("preview.failed");
    }

    [Fact]
    public async Task A_tool_that_ran_out_of_time_says_so()
    {
        var tools = Tools(new Outcome(SandboxOutcome.TimedOut));
        using var pdf = new MemoryStream(ConversionTests.OnePagePdf());

        var render = () => new ToolPageRenderer(tools).RenderPngAsync(pdf, 0, 320);

        (await render.Should().ThrowAsync<PageRenderException>()).Which.Text.Key.Should().Be("conversion.timedOut");
    }
}
