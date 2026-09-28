using System.Text;

using Microsoft.Extensions.Logging;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Pdfium;

namespace NeoPrint.Kiosk.Printing;

/// <summary>Where the kiosk's converters are installed (plan section 7.1: <c>LibreOffice:Path</c>).</summary>
public sealed class ConverterPaths
{
    public string LibreOffice { get; set; } = string.Empty;

    /// <summary>The Ghostscript folder on Windows (<c>...\gs\gs10.x\bin</c>); empty searches PATH.</summary>
    public string GhostscriptFolder { get; set; } = string.Empty;

    public string LibreOfficeExecutable => !string.IsNullOrWhiteSpace(LibreOffice)
        ? LibreOffice
        : OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : "/usr/bin/soffice";

    /// <summary>The Ghostscript family program for an extension (plan section 7.6).</summary>
    public string GhostscriptFor(string extension)
    {
        var name = (extension, OperatingSystem.IsWindows()) switch
        {
            ("xps" or "oxps", true) => "gxpswin64.exe",
            ("xps" or "oxps", false) => "gxps",
            ("pcl" or "prn", true) => "gpcl6win64.exe",
            ("pcl" or "prn", false) => "gpcl6",
            (_, true) => "gswin64c.exe",
            _ => "gs",
        };

        return string.IsNullOrWhiteSpace(GhostscriptFolder) ? name : Path.Combine(GhostscriptFolder, name);
    }
}

/// <summary>A file from the flash drive, ready to show and print.</summary>
/// <param name="PdfPath">The PDF made from it, in a private working folder.</param>
/// <param name="Format">What the file is.</param>
public sealed record PreparedDocument(string PdfPath, int PageCount, FileFormat Format);

/// <summary>
/// Turns a file from the flash drive into a PDF on the kiosk (plan sections
/// 2.2, 7.4): the same format catalogue and the same magic-byte check as the
/// backend, and the same routes.
/// </summary>
/// <remarks>
/// A file is refused by what its first bytes say it is, not by its name
/// (plan section 2.2). Office and page-description files go through the
/// converter runner (a process of its own, with time and memory limits);
/// images and text are done in-process with SkiaSharp, which reads the image
/// header before decoding and refuses oversized images.
/// </remarks>
public sealed class DocumentPreparer
{
    private static readonly TimeSpan ConverterTimeout = TimeSpan.FromSeconds(60);

    private readonly IConverterProcessRunner _runner;
    private readonly ConverterPaths _paths;
    private readonly ILogger<DocumentPreparer> _logger;

    public DocumentPreparer(IConverterProcessRunner runner, ConverterPaths paths, ILogger<DocumentPreparer> logger)
    {
        _runner = runner;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>What a file on the drive is, from its first bytes; the key says why when it is nothing we take.</summary>
    public static (FileFormat? Format, string? RefusalKey) Identify(string path)
    {
        var header = new byte[ContentSignature.HeaderLength];
        int read;

        using (var stream = File.OpenRead(path))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        var result = ContentSignature.Identify(header.AsSpan(0, read), Path.GetFileName(path));

        return result.Recognised ? (result.Format, null) : (null, result.Reason ?? "files.signature.unknown");
    }

    /// <summary>
    /// Copies the file into <paramref name="workFolder"/> and makes a PDF of it.
    /// </summary>
    /// <exception cref="PrintingException">It cannot be shown or printed; the key says why.</exception>
    public async Task<PreparedDocument> PrepareAsync(string sourcePath, string workFolder, CancellationToken cancellationToken)
    {
        var (format, refusal) = Identify(sourcePath);

        if (format is null)
        {
            throw new PrintingException(refusal!);
        }

        // Worked on from a copy: the drive can be pulled out at any moment.
        var input = Path.Combine(workFolder, "source." + format.Extension);
        await using (var from = File.OpenRead(sourcePath))
        await using (var to = File.Create(input))
        {
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
        }

        var pdf = Path.Combine(workFolder, "document.pdf");

        switch (format.Route)
        {
            case ConversionRoute.None:
                File.Copy(input, pdf);
                break;

            case ConversionRoute.Image:
                SkiaDocuments.ImageToPdf(input, pdf);
                break;

            case ConversionRoute.LibreOffice when format.Extension == "txt":
                SkiaDocuments.TextToPdf(await ReadTextAsync(input, cancellationToken).ConfigureAwait(false), pdf);
                break;

            case ConversionRoute.LibreOffice:
                await ConvertWithLibreOfficeAsync(input, workFolder, pdf, cancellationToken).ConfigureAwait(false);
                break;

            case ConversionRoute.Ghostscript:
                await ConvertWithGhostscriptAsync(input, format.Extension, workFolder, pdf, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new PrintingException("conversion.noConverter");
        }

        var pages = PageCount(pdf);

        if (pages < 1)
        {
            throw new PrintingException("pdf.damaged");
        }

        return new PreparedDocument(pdf, pages, format);
    }

    /// <summary>Pages in a PDF: PDFium when it is here, PDFsharp otherwise.</summary>
    public static int PageCount(string pdfPath)
    {
        if (PdfiumLibrary.IsAvailable)
        {
            try
            {
                using var document = PdfDocument.Load(File.ReadAllBytes(pdfPath));
                return document.PageCount;
            }
            catch (PdfiumException exception)
            {
                throw new PrintingException(exception.MessageKey, exception);
            }
        }

        return PdfTools.PageCount(pdfPath);
    }

    /// <summary>Text as the person wrote it: UTF-8, or the Windows code page when it is not valid UTF-8.</summary>
    public static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        return DecodeText(bytes);
    }

    /// <summary>
    /// UTF-8 when the bytes are valid UTF-8; otherwise the Windows code page
    /// the file was most likely saved in: 1251 (Cyrillic) or 1254 (Turkish,
    /// which carries ç ğ ı ö ş ü - Azerbaijani files from older editors).
    /// </summary>
    /// <remarks>
    /// Both pages put their letters in the upper half. In Russian text almost
    /// every letter is there; in Azerbaijani only the few special ones are, so
    /// the share of upper-half bytes among the letters tells them apart.
    /// </remarks>
    public static string DecodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            var upper = bytes.Count(value => value >= 0xC0);
            var ascii = bytes.Count(value => value is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z'));
            var codePage = upper > 0 && upper >= (upper + ascii) * 0.3 ? 1251 : 1254;

            return CodePages.GetEncoding(codePage).GetString(bytes);
        }
    }

    // The code pages ship with .NET but are off until a provider is registered.
    private static class CodePages
    {
        static CodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public static Encoding GetEncoding(int codePage) => Encoding.GetEncoding(codePage);
    }

    private async Task ConvertWithLibreOfficeAsync(string input, string workFolder, string pdf, CancellationToken cancellationToken)
    {
        var profile = Path.Combine(workFolder, "lo-profile");

        string[] arguments =
        [
            "--headless",
            "--invisible",
            "--nologo",
            "--nodefault",
            "--nofirststartwizard",
            "--norestore",
            $"-env:UserInstallation=file:///{profile.Replace('\\', '/').TrimStart('/')}",
            "--convert-to",
            "pdf:writer_pdf_Export",
            "--outdir",
            workFolder,
            input,
        ];

        var run = await _runner.RunAsync(_paths.LibreOfficeExecutable, arguments, workFolder, ConverterTimeout, cancellationToken)
            .ConfigureAwait(false);

        var produced = Path.Combine(workFolder, Path.GetFileNameWithoutExtension(input) + ".pdf");

        if (File.Exists(produced))
        {
            // LibreOffice can exit non-zero having made a good PDF; the file is the answer.
            File.Move(produced, pdf, overwrite: true);
            return;
        }

        throw Failure(run, "LibreOffice");
    }

    private async Task ConvertWithGhostscriptAsync(
        string input, string extension, string workFolder, string pdf, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            // PostScript is a programming language: without this a document
            // can read and write files on the kiosk.
            "-dSAFER",
            "-dBATCH",
            "-dNOPAUSE",
            "-dQUIET",
            "-sDEVICE=pdfwrite",
            $"-sOutputFile={pdf}",
            input,
        ];

        var run = await _runner.RunAsync(_paths.GhostscriptFor(extension), arguments, workFolder, ConverterTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (run.Outcome == ConverterRunOutcome.Exited && run.ExitCode == 0 && File.Exists(pdf) && new FileInfo(pdf).Length > 0)
        {
            return;
        }

        throw Failure(run, "Ghostscript");
    }

    private PrintingException Failure(ConverterRun run, string converter)
    {
        _logger.LogWarning("{Converter} made no PDF ({Outcome}, exit {ExitCode}).", converter, run.Outcome, run.ExitCode);

        return new PrintingException(run.Outcome switch
        {
            ConverterRunOutcome.TimedOut => "conversion.timedOut",
            ConverterRunOutcome.NotStarted => "conversion.notStarted",
            _ => "conversion.failed",
        });
    }
}

/// <summary>A drawn page: BGRA pixels, ready for a bitmap.</summary>
public sealed record RenderedPage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>Pages drawn for the preview and the thumbnails (plan section 7.3, screen 3).</summary>
public static class PageRenderer
{
    /// <summary>Whether pages can be drawn on this kiosk at all (PDFium is installed).</summary>
    public static bool IsAvailable => PdfiumLibrary.IsAvailable;

    /// <param name="pageIndex">Zero-based.</param>
    public static RenderedPage Render(byte[] pdf, int pageIndex, int width)
    {
        using var document = PdfDocument.Load(pdf);
        var (pixels, w, h, stride) = document.RenderPage(pageIndex, width);

        return new RenderedPage(pixels, w, h, stride);
    }
}
