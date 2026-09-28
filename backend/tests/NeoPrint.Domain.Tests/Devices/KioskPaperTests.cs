using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// The paper counter (plan section 2.15): printing takes sheets off, a refill
/// puts them back, and every movement is recorded.
/// </summary>
public sealed class KioskPaperTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Operator = Guid.NewGuid();

    private static Kiosk KioskWith(int capacity, int current)
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };
        kiosk.PaperStock["A4"] = new PaperStock { Capacity = capacity, Current = current };
        return kiosk;
    }

    [Fact]
    public void A_printed_job_takes_its_sheets_off_and_says_so()
    {
        var kiosk = KioskWith(500, 120);
        var job = Guid.NewGuid();

        var movement = kiosk.UsePaper("A4", 7, SupplyEventType.JobPrinted, Now, jobId: job);

        kiosk.PaperStock["A4"].Current.Should().Be(113);
        movement.Should().BeEquivalentTo(new
        {
            Resource = "PaperA4",
            Type = SupplyEventType.JobPrinted,
            Delta = -7,
            Before = 120,
            After = 113,
            JobId = (Guid?)job,
            DedupKey = $"job:{job:N}",
        });
        kiosk.ConfigurationChangedAt.Should().Be(Now, "the kiosk checks orders against the tray and fetches the new count");
    }

    [Fact]
    public void The_counter_stops_at_zero_rather_than_going_negative()
    {
        var kiosk = KioskWith(500, 3);

        var movement = kiosk.UsePaper("A4", 10, SupplyEventType.JobPrinted, Now, jobId: Guid.NewGuid());

        kiosk.PaperStock["A4"].Current.Should().Be(0);
        movement!.Delta.Should().Be(-3, "the record says what the counter did");
    }

    [Fact]
    public void Nothing_used_or_a_size_without_a_counter_is_no_movement()
    {
        var kiosk = KioskWith(500, 100);

        kiosk.UsePaper("A4", 0, SupplyEventType.JobPrinted, Now, jobId: Guid.NewGuid()).Should().BeNull();
        kiosk.UsePaper("A3", 5, SupplyEventType.JobPrinted, Now, jobId: Guid.NewGuid()).Should().BeNull();
    }

    [Theory]
    [InlineData(PaperRefillMode.Add, 200, 320, SupplyEventType.Refill)]
    [InlineData(PaperRefillMode.Full, 0, 500, SupplyEventType.Refill)]
    [InlineData(PaperRefillMode.Exact, 95, 95, SupplyEventType.Recount)]
    public void A_refill_adds_fills_or_sets_the_count(PaperRefillMode mode, int sheets, int after, SupplyEventType type)
    {
        var kiosk = KioskWith(500, 120);

        var movement = kiosk.RefillPaper("A4", mode, sheets, Operator, Now);

        kiosk.PaperStock["A4"].Current.Should().Be(after);
        kiosk.PaperStock["A4"].LastRefillAt.Should().Be(Now);
        kiosk.PaperStock["A4"].LastRefillBy.Should().Be(Operator);
        movement.Type.Should().Be(type);
        movement.Delta.Should().Be(after - 120);
        movement.By.Should().Be(Operator);
    }

    [Theory]
    [InlineData(PaperRefillMode.Add, 400)]
    [InlineData(PaperRefillMode.Exact, 501)]
    [InlineData(PaperRefillMode.Add, 0)]
    [InlineData(PaperRefillMode.Exact, -1)]
    public void A_refill_that_does_not_fit_the_tray_is_refused(PaperRefillMode mode, int sheets)
    {
        var kiosk = KioskWith(500, 120);

        var refill = () => kiosk.RefillPaper("A4", mode, sheets, Operator, Now);

        refill.Should().Throw<DomainException>().Which.Text.Key.Should().Be("kiosks.paperRefillInvalid");
        kiosk.PaperStock["A4"].Current.Should().Be(120);
    }

    [Fact]
    public void A_size_without_a_tray_cannot_be_refilled()
    {
        var kiosk = KioskWith(500, 120);
        kiosk.PaperStock["A3"] = new PaperStock { Capacity = 0 };

        var refill = () => kiosk.RefillPaper("A3", PaperRefillMode.Full, 0, Operator, Now);

        refill.Should().Throw<DomainException>().Which.Text.Key.Should().Be("kiosks.paperNoTray");
    }
}
