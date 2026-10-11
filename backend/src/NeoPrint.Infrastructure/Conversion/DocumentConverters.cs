using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Converter;
using NeoPrint.Documents;
using NeoPrint.Domain.Files;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>What a conversion produced.</summary>
/// <param name="Succeeded">Whether a PDF was written.</param>
/// <param name="PdfPath">The PDF, inside the working directory.</param>
/// <param name="Failure">
/// Why it failed, as a message key the uploader reads in their own language.
/// Never a tool's stderr: that can echo a file path or part of the document.
/// </param>
public sealed record ConversionResult(bool Succeeded, string? PdfPath, string? Failure)
{
    public static ConversionResult Ok(string pdfPath) => new(true, pdfPath, null);

    public static ConversionResult Failed(string failureKey) => new(false, null, failureKey);
}

/// <summary>Turns one family of formats into a PDF.</summary>
public interface IDocumentConverter
{
    /// <summary>Which route this converter handles.</summary>
    ConversionRoute Route { get; }

    /// <summary>
    /// Converts <paramref name="inputPath"/> into a PDF in the same working
    /// directory.
    /// </summary>
    Task<ConversionResult> ConvertAsync(
        string inputPath, SandboxLimits limits, CancellationToken cancellationToken = default);
}

/// <summary>
/// Office documents, spreadsheets, presentations and text, through LibreOffice
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// <para>
/// Run headless in a child process with a profile of its own. The profile
/// matters: LibreOffice shares one per user by default, so two conversions at
/// once fight over it and the second silently attaches to the first instance
/// instead of doing the work.
/// </para>
/// <para>
/// Macros never run. <c>--norestore</c> stops it reopening a document a
/// previous crash left behind, and the macro security level in the generated
/// profile is the highest.
/// </para>
/// </remarks>
public sealed class LibreOfficeConverter : IDocumentConverter
{
    private readonly IProcessSandbox _sandbox;
    private readonly ILogger<LibreOfficeConverter> _logger;
    private readonly string _executable;

    public LibreOfficeConverter(
        IProcessSandbox sandbox, ILogger<LibreOfficeConverter> logger, string? executable = null)
    {
        _sandbox = sandbox;
        _logger = logger;
        _executable = executable ?? DefaultExecutable();
    }

    public ConversionRoute Route => ConversionRoute.LibreOffice;

    public async Task<ConversionResult> ConvertAsync(
        string inputPath, SandboxLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(limits);

        var profile = Path.Combine(limits.WorkingDirectory, "lo-profile");

        string[] arguments =
        [
            "--headless",
            "--invisible",
            "--nologo",
            "--nodefault",
            "--nofirststartwizard",

            // Do not reopen whatever a previous crash left behind: that
            // document would be converted instead of this one.
            "--norestore",

            // A profile per conversion. Without it, concurrent conversions
            // share one and the second attaches to the first instance.
            $"-env:UserInstallation=file:///{profile.Replace('\\', '/').TrimStart('/')}",

            "--convert-to",
            "pdf:writer_pdf_Export",
            "--outdir",
            limits.WorkingDirectory,
            inputPath,
        ];

        var result = await _sandbox.RunAsync(_executable, arguments, limits, cancellationToken)
            .ConfigureAwait(false);

        var expected = Path.Combine(
            limits.WorkingDirectory,
            Path.GetFileNameWithoutExtension(inputPath) + ".pdf");

        if (File.Exists(expected))
        {
            // LibreOffice sometimes exits non-zero having produced a perfectly
            // good PDF - a missing font, an unsupported effect. The file is
            // the answer, not the exit code.
            return ConversionResult.Ok(expected);
        }

        _logger.LogWarning(
            "LibreOffice produced no PDF for {Input} ({Outcome}, exit {ExitCode}). Stderr: {StandardError}",
            Path.GetFileName(inputPath),
            result.Outcome,
            result.ExitCode,
            result.StandardError);

        return ConversionResult.Failed(Describe(result));
    }

    internal static string Describe(SandboxResult result) => result.Outcome switch
    {
        SandboxOutcome.TimedOut => "conversion.timedOut",
        SandboxOutcome.NotStarted => "conversion.notStarted",
        _ => "conversion.failed",
    };

    private static string DefaultExecutable()
        => OperatingSystem.IsWindows()
            ? @"C:\Program Files\LibreOffice\program\soffice.exe"
            : "/usr/bin/soffice";
}

/// <summary>
/// PostScript, PCL and XPS, through the Ghostscript family
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// <c>-dSAFER</c> is the important flag: PostScript is a programming language,
/// and without it a document can read and write files on the machine
/// converting it. It is the default in Ghostscript 9.50 and later, and passed
/// explicitly anyway so that the guarantee does not depend on which version
/// the image happens to carry.
/// </remarks>
public sealed class GhostscriptConverter : IDocumentConverter
{
    private readonly IProcessSandbox _sandbox;
    private readonly ILogger<GhostscriptConverter> _logger;

    public GhostscriptConverter(IProcessSandbox sandbox, ILogger<GhostscriptConverter> logger)
    {
        _sandbox = sandbox;
        _logger = logger;
    }

    public ConversionRoute Route => ConversionRoute.Ghostscript;

    public async Task<ConversionResult> ConvertAsync(
        string inputPath, SandboxLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(limits);

        var output = Path.Combine(limits.WorkingDirectory, "converted.pdf");
        var extension = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();

        // XPS needs its own interpreter; PostScript and PCL have theirs.
        var executable = extension switch
        {
            "xps" or "oxps" => "gxps",
            "pcl" or "prn" => "gpcl6",
            _ => "gs",
        };

        string[] arguments =
        [
            // PostScript is a programming language. Without this, a document
            // can read and write files on the machine converting it.
            "-dSAFER",
            "-dBATCH",
            "-dNOPAUSE",
            "-dQUIET",
            "-sDEVICE=pdfwrite",
            $"-sOutputFile={output}",
            inputPath,
        ];

        var result = await _sandbox.RunAsync(executable, arguments, limits, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded && File.Exists(output) && new FileInfo(output).Length > 0)
        {
            return ConversionResult.Ok(output);
        }

        _logger.LogWarning(
            "{Executable} produced no PDF for {Input} ({Outcome}, exit {ExitCode}). Stderr: {StandardError}",
            executable,
            Path.GetFileName(inputPath),
            result.Outcome,
            result.ExitCode,
            result.StandardError);

        return ConversionResult.Failed(LibreOfficeConverter.Describe(result));
    }
}

/// <summary>
/// Raster images, wrapped into a one-page PDF (plan section 2.2).
/// </summary>
/// <remarks>
/// Decoded by the converter tool (SkiaSharp), like every other parser of a
/// user's file - in the converter container, not in this process
/// (docs/adr/ADR-0005-isolated-converter.md). The tool checks the pixel count
/// from the header before it decodes anything.
/// </remarks>
public sealed class ImageConverter : IDocumentConverter
{
    private const string Output = "converted.pdf";

    private readonly ConverterTools _tools;
    private readonly ILogger<ImageConverter> _logger;

    public ImageConverter(ConverterTools tools, ILogger<ImageConverter> logger)
    {
        _tools = tools;
        _logger = logger;
    }

    public ConversionRoute Route => ConversionRoute.Image;

    public async Task<ConversionResult> ConvertAsync(
        string inputPath, SandboxLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(limits);

        try
        {
            var response = await _tools.RunAsync(
                    limits.WorkingDirectory,
                    ToolContract.ImageToPdf,
                    new ToolRequest(Path.GetFileName(inputPath), Output),
                    limits.Timeout,
                    failFast: false,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.Ok)
            {
                return ConversionResult.Ok(Path.Combine(limits.WorkingDirectory, Output));
            }

            _logger.LogWarning(
                "Converting {Input} to PDF failed: {Reason}.", Path.GetFileName(inputPath), response.Error ?? "the tool failed");

            return ConversionResult.Failed(response.Error ?? "conversion.imageToPdfFailed");
        }
        catch (FileProcessingUnavailableException)
        {
            return ConversionResult.Failed("conversion.notStarted");
        }
    }
}
