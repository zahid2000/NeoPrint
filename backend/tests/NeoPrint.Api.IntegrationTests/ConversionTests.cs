using System.Text;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Infrastructure.Conversion;
using NeoPrint.Pdfium;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Turning an uploaded file into the PDF everything downstream uses
/// (plan section 2.2).
/// </summary>
/// <remarks>
/// The PDF and image routes run wherever PDFium is available. The LibreOffice
/// and Ghostscript routes need those tools, which only the backend image has;
/// what is checked here is that their absence is reported as a conversion
/// failure on the file rather than an exception in a request.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ConversionTests
{
    private readonly NeoPrintApiFixture _fixture;

    public ConversionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A minimal but genuinely valid one-page PDF, built by hand.
    /// </summary>
    /// <remarks>
    /// PDFium is strict about the cross-reference table, so this is a real
    /// document rather than a header and a marker: the point is to exercise
    /// the parser, not to work around it.
    /// </remarks>
    internal static byte[] OnePagePdf(int pages = 1)
    {
        var builder = new StringBuilder();
        var offsets = new List<int>();

        void Object(string body)
        {
            offsets.Add(builder.Length);
            builder.Append(body);
        }

        builder.Append("%PDF-1.4\n");

        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"));

        Object($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Object($"2 0 obj\n<< /Type /Pages /Count {pages} /Kids [{kids}] >>\nendobj\n");

        for (var i = 0; i < pages; i++)
        {
            Object(
                $"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
                + ">> \nendobj\n");
        }

        var xrefPosition = builder.Length;
        var count = offsets.Count + 1;

        builder.Append($"xref\n0 {count}\n");
        builder.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>A 4x4 PNG, encoded by Skia so it is a real image.</summary>
    private static byte[] SmallPng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(4, 4);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

        return data.ToArray();
    }

    private async Task<StoredFile> UploadAsync(
        IServiceScope scope, byte[] content, string fileName, string extension, string mediaType)
    {
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        var file = new StoredFile
        {
            UserId = Guid.NewGuid(),
            FileName = fileName,
            Extension = extension,
            MediaType = mediaType,
            Source = FileSource.Uploaded,
        };

        var stored = await storage.WriteAsync(
            StorageBucket.Files,
            $"files/{file.UserId:N}/{file.Id:N}/original.{extension}",
            new MemoryStream(content),
            mediaType);

        file.Original = new FileObject(
            stored.ProviderId,
            stored.Key,
            stored.Sha256,
            stored.EncryptedLength,
            stored.PlaintextLength,
            stored.KekId);

        await files.AddAsync(file);

        return file;
    }

    [PdfiumFact]
    public async Task A_pdf_is_counted_without_being_converted_again()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        var file = await UploadAsync(scope, OnePagePdf(3), "hesabat.pdf", "pdf", "application/pdf");

        await conversion.ConvertAsync(file.Id);

        var converted = await files.FindByIdAsync(file.Id);

        converted!.Status.Should().Be(FileStatus.Ready, converted.FailureReason);
        converted.PageCount.Should().Be(3);

        // The same object, not a second copy: storing the bytes twice for a
        // file that is already a PDF would double what every account uses.
        converted.Pdf!.Key.Should().Be(converted.Original.Key);
    }

    [PdfiumFact]
    public async Task An_image_becomes_a_one_page_pdf()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        var file = await UploadAsync(scope, SmallPng(), "şəkil.png", "png", "image/png");

        await conversion.ConvertAsync(file.Id);

        var converted = await files.FindByIdAsync(file.Id);

        converted!.Status.Should().Be(FileStatus.Ready, converted.FailureReason);
        converted.PageCount.Should().Be(1);

        // A separate object this time, and a real PDF.
        converted.Pdf!.Key.Should().NotBe(converted.Original.Key);

        using var pdf = new MemoryStream();
        await storage.ReadAsync(
            new StoredObject(
                converted.Pdf.ProviderId,
                converted.Pdf.Key,
                converted.Pdf.Sha256,
                converted.Pdf.EncryptedLength,
                converted.Pdf.PlaintextLength,
                converted.Pdf.KekId),
            StorageBucket.Files,
            pdf);

        using var document = PdfDocument.Load(pdf.ToArray());
        document.PageCount.Should().Be(1);
    }

    [PdfiumFact]
    public async Task A_corrupt_pdf_fails_with_something_the_user_can_act_on()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        // Right header, nothing behind it.
        var file = await UploadAsync(
            scope, Encoding.ASCII.GetBytes("%PDF-1.4\nnot really a pdf\n"), "zədəli.pdf", "pdf", "application/pdf");

        await conversion.ConvertAsync(file.Id);

        var converted = await files.FindByIdAsync(file.Id);

        converted!.Status.Should().Be(FileStatus.Failed);

        // A sentence, not a stack trace or a PDFium error code.
        converted.FailureReason.Should().NotBeNullOrWhiteSpace();
        converted.FailureReason.Should().NotContain("Exception");
        converted.FailureReason.Should().NotContain("PDFium");
    }

    [PdfiumFact]
    public async Task Converting_the_same_file_twice_does_the_work_once()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        var file = await UploadAsync(scope, OnePagePdf(2), "təkrar.pdf", "pdf", "application/pdf");

        await conversion.ConvertAsync(file.Id);
        var first = await files.FindByIdAsync(file.Id);

        // A redelivered message must not convert it again
        // (docs/IDEMPOTENCY.md).
        await conversion.ConvertAsync(file.Id);
        var second = await files.FindByIdAsync(file.Id);

        second!.Status.Should().Be(FileStatus.Ready);
        second.PageCount.Should().Be(first!.PageCount);
        second.RowVersion.Should().Be(first.RowVersion);
    }

    [PdfiumFact]
    public async Task A_document_over_the_page_limit_is_refused_after_conversion()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        var original = await ReadLimitAsync(scope);

        try
        {
            await settings.UpdateAsync(
                new Dictionary<string, string> { ["Files.DefaultMaxPages"] = "2" },
                updatedBy: null,
                CancellationToken.None);

            var file = await UploadAsync(scope, OnePagePdf(5), "böyük.pdf", "pdf", "application/pdf");

            await conversion.ConvertAsync(file.Id);

            var converted = await files.FindByIdAsync(file.Id);

            // The count is not knowable at upload - a 2 MB spreadsheet can be
            // four thousand pages - so the limit lands here.
            converted!.Status.Should().Be(FileStatus.Failed);
            Messages.WriteStored(converted.FailureReason, "az").Should().Contain("5 səhifə");
        }
        finally
        {
            await settings.UpdateAsync(
                new Dictionary<string, string> { ["Files.DefaultMaxPages"] = original },
                updatedBy: null,
                CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task A_missing_converter_is_recorded_on_the_file_rather_than_thrown()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        // LibreOffice is not installed outside the backend image. A missing
        // converter is an operational problem; the upload still has to end in
        // a state the user can see.
        var file = await UploadAsync(
            scope,
            Encoding.ASCII.GetBytes("Salam, dünya!"),
            "qeydlər.txt",
            "txt",
            "text/plain");

        var convert = async () => await conversion.ConvertAsync(file.Id);

        await convert.Should().NotThrowAsync();

        var converted = await files.FindByIdAsync(file.Id);

        converted!.Status.Should().BeOneOf(FileStatus.Ready, FileStatus.Failed);

        if (converted.Status == FileStatus.Failed)
        {
            converted.FailureReason.Should().NotBeNullOrWhiteSpace();
        }
    }

    [DockerFact]
    public async Task A_deleted_file_is_not_converted()
    {
        using var scope = _fixture.Services.CreateScope();
        var conversion = scope.ServiceProvider.GetRequiredService<DocumentConversionService>();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        var file = await UploadAsync(scope, OnePagePdf(), "silinib.pdf", "pdf", "application/pdf");

        file.DeletedAt = DateTime.UtcNow;
        await files.UpdateAsync(file);

        // The message can arrive after the user deleted the file; converting
        // it would put bytes back that were meant to be gone.
        var convert = async () => await conversion.ConvertAsync(file.Id);

        await convert.Should().NotThrowAsync();

        (await files.FindByIdAsync(file.Id))!.Status.Should().Be(FileStatus.Pending);
    }

    private static async Task<string> ReadLimitAsync(IServiceScope scope)
    {
        var reader = scope.ServiceProvider.GetRequiredService<ISettingsReader>();

        return (await reader.GetAsync("Files.DefaultMaxPages"))?.Trim() ?? "200";
    }
}
