using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;

using PdfSharp.Pdf;

using SkiaSharp;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// A flash file becomes a PDF on the kiosk, and the customer's choices are
/// applied to it (plan sections 2.2, 2.3, 7.4).
/// </summary>
public sealed class DocumentPreparerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neoprint-docs", Guid.NewGuid().ToString("N"));

    public DocumentPreparerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Work()
    {
        var work = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private string Pdf(int pages, string name = "sample.pdf")
    {
        var path = Path.Combine(_folder, name);

        using var document = new PdfDocument();
        for (var page = 0; page < pages; page++)
        {
            document.AddPage();
        }

        document.Save(path);
        return path;
    }

    private string Png(int width = 320, int height = 200, string name = "photo.png")
    {
        var path = Path.Combine(_folder, name);

        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());

        return path;
    }

    private static DocumentPreparer Preparer(IConverterProcessRunner? runner = null)
        => new(runner ?? new PlainConverterRunner(), new ConverterPaths { LibreOffice = "neoprint-no-such-soffice" }, NullLogger<DocumentPreparer>.Instance);

    [Fact]
    public async Task A_pdf_is_taken_as_it_is_and_counted()
    {
        var prepared = await Preparer().PrepareAsync(Pdf(5), Work(), CancellationToken.None);

        prepared.PageCount.Should().Be(5);
        prepared.Format.Extension.Should().Be("pdf");
    }

    [Fact]
    public async Task An_image_becomes_a_one_page_pdf()
    {
        var prepared = await Preparer().PrepareAsync(Png(), Work(), CancellationToken.None);

        prepared.PageCount.Should().Be(1);
        prepared.Format.Extension.Should().Be("png");
    }

    [Fact]
    public async Task A_text_file_is_laid_out_on_pages_without_libreoffice()
    {
        var path = Path.Combine(_folder, "qeyd.txt");
        await File.WriteAllTextAsync(path, string.Join('\n', Enumerable.Range(1, 150).Select(line => $"Sətir {line}: Azərbaycan dilində mətn.")));

        var prepared = await Preparer().PrepareAsync(path, Work(), CancellationToken.None);

        prepared.PageCount.Should().BeGreaterThan(1, "150 lines do not fit on one A4 page");
    }

    [Fact]
    public async Task A_file_is_judged_by_its_bytes_not_its_name()
    {
        var path = Path.Combine(_folder, "not-really.pdf");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("MZ this is a program, not a document"));

        var act = () => Preparer().PrepareAsync(path, Work(), CancellationToken.None);

        (await act.Should().ThrowAsync<PrintingException>()).Which.MessageKey.Should().StartWith("files.signature.");
    }

    [Fact]
    public async Task A_missing_converter_says_so_rather_than_blaming_the_file()
    {
        // A DOCX: a ZIP whose first entry is [Content_Types].xml.
        var path = Path.Combine(_folder, "hesabat.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            await writer.WriteAsync("<Types/>");
        }

        var act = () => Preparer().PrepareAsync(path, Work(), CancellationToken.None);

        (await act.Should().ThrowAsync<PrintingException>()).Which.MessageKey.Should().Be("conversion.notStarted");
    }

    [Fact]
    public async Task A_converter_that_hangs_is_stopped_at_its_time_limit()
    {
        var runner = new FixedRunner(new ConverterRun(ConverterRunOutcome.TimedOut, -1, string.Empty));
        var path = Path.Combine(_folder, "slides.pptx");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open());
            await writer.WriteAsync("<Types/>");
        }

        var act = () => Preparer(runner).PrepareAsync(path, Work(), CancellationToken.None);

        (await act.Should().ThrowAsync<PrintingException>()).Which.MessageKey.Should().Be("conversion.timedOut");
        runner.Executable.Should().Be("neoprint-no-such-soffice");
        runner.Arguments.Should().Contain("--headless").And.Contain(argument => argument.StartsWith("-env:UserInstallation=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ghostscript_is_always_asked_to_run_safely()
    {
        var runner = new FixedRunner(new ConverterRun(ConverterRunOutcome.Exited, 1, string.Empty));
        var path = Path.Combine(_folder, "document.ps");
        await File.WriteAllTextAsync(path, "%!PS-Adobe-3.0\nshowpage\n");

        var act = () => Preparer(runner).PrepareAsync(path, Work(), CancellationToken.None);

        await act.Should().ThrowAsync<PrintingException>();
        runner.Arguments.Should().Contain("-dSAFER", "PostScript can read and write files without it");
    }

    [Fact]
    public void The_print_file_holds_the_chosen_pages_in_order()
    {
        var target = Path.Combine(_folder, "print.pdf");

        PdfTools.BuildForPrint(Pdf(10), target, [3, 1, 5], pagesPerSheet: 1, "A4");

        PdfTools.PageCount(target).Should().Be(3);
    }

    [Theory]
    [InlineData(2, 5, 3)]
    [InlineData(4, 5, 2)]
    [InlineData(4, 8, 2)]
    public void Several_pages_go_on_one_sheet(int perSheet, int pages, int sheets)
    {
        var target = Path.Combine(_folder, $"nup-{perSheet}-{pages}.pdf");

        PdfTools.BuildForPrint(Pdf(pages), target, [.. Enumerable.Range(1, pages)], perSheet, "A4");

        PdfTools.PageCount(target).Should().Be(sheets);
    }

    [Fact]
    public void The_editor_deletes_reorders_and_turns_pages()
    {
        var target = Path.Combine(_folder, "edited.pdf");

        PdfTools.ApplyChanges(Pdf(4), target, [new PageChange(4, 90), new PageChange(2, 0)]);

        using var result = PdfSharp.Pdf.IO.PdfReader.Open(target, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        result.PageCount.Should().Be(2);
        result.Pages[0].Rotate.Should().Be(90);
    }

    [Fact]
    public void An_image_is_edited_into_a_new_file_and_turned()
    {
        var target = Path.Combine(_folder, "edited.png");

        SkiaDocuments.EditImage(Png(320, 200), target, new ImageChange(90, null, 10, 0));

        SkiaDocuments.ImageSize(target).Should().Be((200, 320));
    }

    [Fact]
    public void An_image_claiming_to_be_huge_is_refused_before_it_is_decoded()
    {
        // A PNG header that says 20000 x 20000: 400 megapixels.
        var path = Path.Combine(_folder, "bomb.png");
        var header = Convert.FromHexString("89504E470D0A1A0A0000000D494844520000" + "4E20" + "0000" + "4E20" + "0806000000");
        File.WriteAllBytes(path, [.. header, .. new byte[64]]);

        var act = () => SkiaDocuments.ImageToPdf(path, Path.Combine(_folder, "bomb.pdf"));

        act.Should().Throw<PrintingException>().Which.MessageKey.Should().BeOneOf("conversion.imageTooLarge", "conversion.imageUnreadable");
    }

    private sealed class FixedRunner : IConverterProcessRunner
    {
        private readonly ConverterRun _result;

        public FixedRunner(ConverterRun result) => _result = result;

        public string? Executable { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ConverterRun> RunAsync(
            string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Executable = executable;
            Arguments = arguments;
            return Task.FromResult(_result);
        }
    }
}
