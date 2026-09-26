using System.Text;

using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Tests;

/// <summary>
/// Identifying a file by its bytes rather than its name (plan section 2.2).
/// </summary>
/// <remarks>
/// The rule exists because an extension is a claim the uploader makes.
/// Trusting it means a .jpg that is really a PostScript program reaches
/// Ghostscript, and a format an administrator disabled is the one that gets
/// processed anyway.
/// </remarks>
public sealed class ContentSignatureTests
{
    private static SignatureResult Identify(byte[] header, string fileName)
        => ContentSignature.Identify(header, fileName);

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void A_pdf_is_recognised_by_its_header()
    {
        var result = Identify(Bytes("%PDF-1.7\n%âãÏÓ"), "report.pdf");

        result.Recognised.Should().BeTrue();
        result.Format!.Extension.Should().Be("pdf");
        result.Format.Route.Should().Be(ConversionRoute.None);
    }

    [Fact]
    public void A_postscript_file_renamed_to_jpg_is_still_postscript()
    {
        // The whole point: the content decides, so this reaches Ghostscript
        // as PostScript rather than SkiaSharp as an image.
        var result = Identify(Bytes("%!PS-Adobe-3.0\n"), "holiday-photo.jpg");

        result.Recognised.Should().BeTrue();
        result.Format!.Extension.Should().Be("ps");
        result.Format.Route.Should().Be(ConversionRoute.Ghostscript);
    }

    [Fact]
    public void An_executable_renamed_to_pdf_is_refused()
    {
        // MZ: a Windows executable.
        var result = Identify([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00], "invoice.pdf");

        result.Recognised.Should().BeFalse();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_executable_renamed_to_txt_is_refused()
    {
        // Text formats have no signature, so this is the case that would slip
        // through a naive "no signature means text" rule.
        var result = Identify([0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01], "notes.txt");

        result.Recognised.Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "photo.jpg", "jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "logo.png", "png")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x36 }, "scan.bmp", "bmp")]
    public void Images_are_recognised_by_signature(byte[] header, string name, string expected)
    {
        var result = Identify(header, name);

        result.Format?.Extension.Should().Be(expected);
        result.Format?.Route.Should().Be(ConversionRoute.Image);
    }

    [Fact]
    public void A_gif_is_recognised_in_both_versions()
    {
        Identify(Bytes("GIF87a"), "a.gif").Format?.Extension.Should().Be("gif");
        Identify(Bytes("GIF89a"), "a.gif").Format?.Extension.Should().Be("gif");
    }

    [Fact]
    public void A_webp_needs_more_than_the_riff_header()
    {
        // RIFF alone is also a WAV file, so the form is checked too.
        Identify(Bytes("RIFF....WEBPVP8 "), "image.webp").Format?.Extension.Should().Be("webp");
        Identify(Bytes("RIFF....WAVEfmt "), "sound.webp").Recognised.Should().BeFalse();
    }

    [Fact]
    public void An_ooxml_document_is_recognised_and_the_extension_says_which_one()
    {
        // docx, xlsx and pptx are the same container with different parts.
        var zip = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
        zip.AddRange(Bytes(new string(' ', 26) + "[Content_Types].xml"));

        Identify([.. zip], "report.docx").Format?.Extension.Should().Be("docx");
        Identify([.. zip], "budget.xlsx").Format?.Extension.Should().Be("xlsx");
        Identify([.. zip], "deck.pptx").Format?.Extension.Should().Be("pptx");
    }

    [Fact]
    public void An_ooxml_document_with_an_extension_from_another_family_is_refused()
    {
        var zip = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
        zip.AddRange(Bytes(new string(' ', 26) + "[Content_Types].xml"));

        // .odt is OpenDocument, not OOXML: the container and the claim
        // disagree, and guessing which is right would be worse than refusing.
        Identify([.. zip], "report.odt").Recognised.Should().BeFalse();
    }

    [Fact]
    public void An_opendocument_file_is_recognised()
    {
        var zip = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
        zip.AddRange(Bytes(new string(' ', 26) + "mimetypeapplication/vnd.oasis.opendocument.text"));

        Identify([.. zip], "letter.odt").Format?.Extension.Should().Be("odt");
    }

    [Fact]
    public void A_plain_zip_is_refused()
    {
        var zip = new List<byte> { 0x50, 0x4B, 0x03, 0x04 };
        zip.AddRange(Bytes(new string(' ', 26) + "photos/holiday.jpg"));

        Identify([.. zip], "archive.docx").Recognised.Should().BeFalse();
    }

    [Fact]
    public void An_old_office_file_needs_its_extension_to_say_which_one()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

        Identify(ole, "report.doc").Format?.Extension.Should().Be("doc");
        Identify(ole, "budget.xls").Format?.Extension.Should().Be("xls");

        // A compound file claiming to be something it cannot be.
        Identify(ole, "photo.png").Recognised.Should().BeFalse();
    }

    [Fact]
    public void Text_formats_are_accepted_when_the_content_is_text()
    {
        Identify(Bytes("Salam, dünya!\r\nİkinci sətir.\n"), "notes.txt")
            .Format?.Extension.Should().Be("txt");

        Identify(Bytes("ad,soyad,yaş\nƏli,Məmmədov,30\n"), "people.csv")
            .Format?.Extension.Should().Be("csv");

        Identify(Bytes("<!DOCTYPE html><html><body>Salam</body></html>"), "page.html")
            .Format?.Extension.Should().Be("html");
    }

    [Fact]
    public void Text_with_a_nul_byte_is_not_text()
    {
        var withNul = Bytes("Salam").Concat<byte>([0x00]).Concat(Bytes("dünya")).ToArray();

        Identify(withNul, "notes.txt").Recognised.Should().BeFalse();
    }

    [Fact]
    public void An_empty_file_is_refused_with_a_reason()
    {
        var result = Identify([], "empty.pdf");

        result.Recognised.Should().BeFalse();
        result.Reason.Should().Contain("boş");
    }

    [Fact]
    public void A_pcl_stream_is_recognised()
    {
        // What a print driver emits: a PJL header, or a bare PCL reset.
        Identify([0x1B, (byte)'%', (byte)'-', (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'X'], "job.pcl")
            .Format?.Extension.Should().Be("pcl");

        Identify([0x1B, (byte)'E', 0x1B, (byte)'&'], "job.prn")
            .Format?.Extension.Should().Be("pcl");
    }

    [Fact]
    public void An_rtf_document_is_recognised()
        => Identify(Bytes(@"{\rtf1\ansi"), "letter.rtf").Format?.Extension.Should().Be("rtf");

    [Fact]
    public void Every_format_in_the_catalogue_has_a_route_and_a_media_type()
    {
        foreach (var format in FileFormats.All)
        {
            format.MediaType.Should().NotBeNullOrWhiteSpace(format.Extension);
            format.Extension.Should().Be(format.Extension.ToLowerInvariant());

            // Only PDF needs no conversion; everything else has to name how it
            // becomes one, or the pipeline has nowhere to send it.
            if (format.Extension != "pdf")
            {
                format.Route.Should().NotBe(ConversionRoute.None, format.Extension);
            }
        }
    }

    [Fact]
    public void The_catalogue_covers_the_plan_list()
    {
        // Plan section 2.2's MVP table. A format dropped by accident would
        // otherwise only show up when somebody tried to upload one.
        string[] expected =
        [
            "pdf",
            "doc", "docx", "docm", "dot", "dotx", "odt", "ott", "rtf", "wps", "txt", "html", "htm", "xml",
            "xls", "xlsx", "xlsm", "xltx", "ods", "csv",
            "ppt", "pptx", "pps", "ppsx", "potx", "odp",
            "jpg", "jpeg", "png", "bmp", "gif", "webp",
            "ps", "eps", "pcl", "prn", "xps", "oxps",
        ];

        FileFormats.Extensions.Should().BeEquivalentTo(expected);
    }
}
