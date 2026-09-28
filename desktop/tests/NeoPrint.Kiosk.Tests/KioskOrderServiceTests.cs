using System.Text;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The flash order from the kiosk's side: the quote on the options screen,
/// and placing the order into the spool and the queue (plan sections 7.3–7.5).
/// </summary>
public sealed class KioskOrderServiceTests
{
    private static readonly FileFormat Pdf = FileFormats.Find("pdf")!;

    [Fact]
    public async Task The_quote_follows_the_kiosks_price_table()
    {
        using var kit = await OrderTestKit.CreateAsync();

        var quote = kit.Orders.Quote(new JobOptions(Copies: 2, PageRange: "1-5"), pageCount: 10);

        quote.Should().NotBeNull();
        quote!.SelectedPages.Should().Be(5);
        quote.Sheets.Should().Be(10);
        quote.Price.FinalPrice.Should().Be(1.00m);
        quote.Price.Currency.Should().Be("AZN");
        quote.PaperCovers.Should().BeTrue();
    }

    [Fact]
    public async Task A_range_outside_the_document_has_no_quote()
    {
        using var kit = await OrderTestKit.CreateAsync();

        kit.Orders.Quote(new JobOptions(PageRange: "8-12"), pageCount: 5).Should().BeNull();
    }

    [Fact]
    public async Task An_order_bigger_than_the_tray_is_flagged_when_the_kiosk_blocks_on_paper()
    {
        using var blocking = await OrderTestKit.CreateAsync(paperCurrent: 20);
        using var lenient = await OrderTestKit.CreateAsync(paperCurrent: 20, blockOnPaper: false);
        var options = new JobOptions(Copies: 3);

        blocking.Orders.Quote(options, pageCount: 10)!.PaperCovers.Should().BeFalse();
        lenient.Orders.Quote(options, pageCount: 10)!.PaperCovers.Should().BeTrue();
    }

    [Fact]
    public async Task A_placed_order_is_in_the_spool_encrypted_and_queued_in_the_journal()
    {
        using var kit = await OrderTestKit.CreateAsync();
        var document = new PreparedDocument(kit.Pdf(4), 4, Pdf);

        var job = await kit.Orders.PlaceAsync(document, "report.pdf", new JobOptions(PageRange: "2-3"), "order-1", CancellationToken.None);

        kit.Backend.IdempotencyKeys.Should().Equal("order-1");
        kit.Backend.Bodies.Should().ContainSingle().Which.Should().Contain("\"dev\":true");
        kit.Spool.Exists(JournalEntry.SpoolNameFor(job.Id)).Should().BeTrue();

        // Encrypted at rest: the spool file is not a PDF anyone can open.
        var stored = Directory.GetFiles(kit.Paths.SpoolDirectory, "*", SearchOption.AllDirectories)
            .Single(path => path.Contains(job.Id.ToString("N"), StringComparison.Ordinal));
        Encoding.ASCII.GetString(await File.ReadAllBytesAsync(stored), 0, 5).Should().NotBe("%PDF-");

        kit.Journal.All().Should().ContainSingle(entry => entry.JobId == job.Id && entry.State == JournalState.Queued);
        Directory.GetFileSystemEntries(kit.Spool.WorkFolder).Should().BeEmpty("the plain print file is removed once it is encrypted");
    }

    [Fact]
    public async Task An_order_whose_print_file_cannot_be_made_is_cancelled_at_the_backend()
    {
        using var kit = await OrderTestKit.CreateAsync();
        var broken = Path.Combine(kit.Paths.Root, "broken.pdf");
        await File.WriteAllTextAsync(broken, "not a pdf at all");

        var place = () => kit.Orders.PlaceAsync(new PreparedDocument(broken, 4, Pdf), "broken.pdf", new JobOptions(), "order-2", CancellationToken.None);

        await place.Should().ThrowAsync<Exception>();
        kit.Backend.Cancelled.Should().ContainSingle();
        kit.Journal.All().Should().BeEmpty();
    }

    [Fact]
    public async Task An_untouched_pdf_goes_as_the_original_to_a_printer_that_takes_pdf()
    {
        using var kit = await OrderTestKit.CreateAsync();
        var source = kit.Pdf(3);

        var job = await kit.Orders.PlaceAsync(
            new PreparedDocument(kit.Pdf(3), 3, Pdf, source), "report.pdf", new JobOptions(Copies: 2, Duplex: true), "order-3", CancellationToken.None);

        (await SpooledBytesAsync(kit, job.Id, PrintContentTypes.Pdf)).Should().Equal(await File.ReadAllBytesAsync(source));
        kit.Journal.All().Should().ContainSingle().Which.ContentType.Should().Be(PrintContentTypes.Pdf);
    }

    [Theory]
    [InlineData("2-3", 1, false, true)]
    [InlineData(null, 2, false, true)]
    [InlineData(null, 1, true, true)]
    [InlineData(null, 1, false, false)]
    public async Task A_changed_file_or_a_printer_without_the_format_gets_the_prepared_pdf(
        string? range, int perSheet, bool edited, bool printerTakesPdf)
    {
        using var kit = await OrderTestKit.CreateAsync();

        if (!printerTakesPdf)
        {
            kit.Printer.NativeFormats.Clear();
        }

        var source = kit.Pdf(3);
        var document = new PreparedDocument(kit.Pdf(3), 3, Pdf, source, edited);

        var original = await kit.Orders.OriginalFormatAsync(document, new JobOptions(PageRange: range, PagesPerSheet: perSheet), CancellationToken.None);
        var job = await kit.Orders.PlaceAsync(document, "report.pdf", new JobOptions(PageRange: range, PagesPerSheet: perSheet), "order-4", CancellationToken.None);

        original.Should().BeNull();
        kit.Journal.All().Should().ContainSingle().Which.ContentType.Should().BeNull();
        (await SpooledBytesAsync(kit, job.Id, null)).Should().NotEqual(await File.ReadAllBytesAsync(source));
    }

    [Fact]
    public async Task A_postscript_file_goes_as_postscript_only_to_a_printer_that_speaks_it()
    {
        using var kit = await OrderTestKit.CreateAsync();
        var postScript = Path.Combine(kit.Paths.Root, "flyer.ps");
        await File.WriteAllTextAsync(postScript, "%!PS-Adobe-3.0\nshowpage\n");
        var document = new PreparedDocument(kit.Pdf(1), 1, FileFormats.Find("ps")!, postScript);

        (await kit.Orders.OriginalFormatAsync(document, new JobOptions(), CancellationToken.None)).Should().BeNull("the printer lists PDF only");

        kit.Printer.NativeFormats.Add(PrintContentTypes.PostScript);
        var job = await kit.Orders.PlaceAsync(document, "flyer.ps", new JobOptions(), "order-5", CancellationToken.None);

        kit.Journal.All().Should().ContainSingle().Which.SpoolName.Should().EndWith(".ps");
        (await SpooledBytesAsync(kit, job.Id, PrintContentTypes.PostScript)).Should().Equal(await File.ReadAllBytesAsync(postScript));
    }

    private static async Task<byte[]> SpooledBytesAsync(OrderTestKit kit, Guid jobId, string? contentType)
    {
        var plain = await kit.Spool.OpenAsync(JournalEntry.SpoolNameFor(jobId, contentType));

        try
        {
            return await File.ReadAllBytesAsync(plain);
        }
        finally
        {
            kit.Spool.Discard(plain);
        }
    }
}
