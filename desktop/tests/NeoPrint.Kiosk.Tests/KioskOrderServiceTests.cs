using System.Text;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
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
}
