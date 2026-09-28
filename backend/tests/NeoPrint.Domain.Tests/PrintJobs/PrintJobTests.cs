using NeoPrint.Documents;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Domain.Tests.PrintJobs;

/// <summary>The print job state machine (docs/STATE_MACHINES.md, "PrintJob").</summary>
public sealed class PrintJobTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext Kiosk = ActorContext.Kiosk(Guid.NewGuid());

    private static PrintJob NewJob(int pages = 4, PrintOptions? options = null)
    {
        var profile = new PricingProfile
        {
            Name = "Standart",
            IsDefault = true,
            BwPricePerSide = Money.FromAzn(0.10m),
            ColorPricePerSide = Money.FromAzn(0.30m),
            MinOrder = Money.FromAzn(0.20m),
            PaperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = 2m },
        };

        options ??= PrintOptions.Default;

        return PrintJob.Create("123456", Guid.NewGuid(), PrintJobSource.Flash, pages, options,
            PriceSnapshot.Take(profile, options.ToSpec(pages), Now), Now, localFileName: "a.pdf");
    }

    private static PrintJob Queued(int pages = 4)
    {
        var job = NewJob(pages);
        job.SkipPaymentForDevelopment(Kiosk, Now);
        job.Enqueue(Kiosk, Now);
        return job;
    }

    [Fact]
    public void A_new_job_is_a_draft_with_its_price_and_sheets()
    {
        var job = NewJob(pages: 5, PrintOptions.Default with { Copies = 2, Duplex = true });

        job.Status.Should().Be(PrintJobStatus.Draft);
        job.Price.Total.Amount.Should().Be(1.00m, "10 sides at 0.10");
        job.RequiredSheets.Should().Be(6, "each copy of 5 pages on both sides takes 3 sheets");
    }

    [Fact]
    public void The_happy_path_records_every_step()
    {
        var job = NewJob();

        job.AwaitPayment(Kiosk, Now);
        job.MarkPaid(Guid.NewGuid(), Kiosk, Now);
        job.Enqueue(Kiosk, Now);
        job.StartPrinting(Kiosk, Now);
        job.Complete(4, 4, Kiosk, Now);

        job.Status.Should().Be(PrintJobStatus.Completed);
        job.PendingTransitions.Select(step => $"{step.FromStatus}->{step.ToStatus}").Should().Equal(
            "Draft->PendingPayment", "PendingPayment->Paid", "Paid->Queued", "Queued->Printing", "Printing->Completed");
        job.Attempts.Should().Be(1);
    }

    [Fact]
    public void A_job_cannot_be_printed_without_being_paid()
    {
        var job = NewJob();
        job.AwaitPayment(Kiosk, Now);

        var act = () => job.Enqueue(Kiosk, Now);

        act.Should().Throw<IllegalStateTransitionException>();
    }

    [Fact]
    public void A_completed_job_never_prints_again()
    {
        var job = Queued();
        job.StartPrinting(Kiosk, Now);
        job.Complete(4, 4, Kiosk, Now);

        ((Action)(() => job.StartPrinting(Kiosk, Now))).Should().Throw<IllegalStateTransitionException>();
        ((Action)(() => job.Enqueue(Kiosk, Now))).Should().Throw<IllegalStateTransitionException>();
    }

    [Fact]
    public void The_same_report_twice_counts_once()
    {
        var job = Queued();
        job.StartPrinting(Kiosk, Now);
        job.Complete(4, 4, Kiosk, Now);
        job.Complete(1, 1, Kiosk, Now);

        job.PrintedSheets.Should().Be(4, "the first report counted");
        job.PendingTransitions.Count(step => step.ToStatus == "Completed").Should().Be(1);
    }

    [Fact]
    public void An_interrupted_job_can_go_back_into_the_queue_and_counts_an_attempt()
    {
        var job = Queued();
        job.StartPrinting(Kiosk, Now);
        job.Interrupt(new UserMessage("printer.paperJam"), 1, 1, Kiosk, Now);

        job.ErrorReason.Should().Be("printer.paperJam");

        job.Enqueue(Kiosk, Now.AddMinutes(5));
        job.StartPrinting(Kiosk, Now.AddMinutes(6));

        job.Status.Should().Be(PrintJobStatus.Printing);
        job.Attempts.Should().Be(2);
        job.ErrorReason.Should().BeNull("a new attempt starts clean");
    }

    [Fact]
    public void A_suspected_job_leaves_only_with_new_information()
    {
        var job = Queued();
        job.StartPrinting(Kiosk, Now);
        job.MarkSuspected(ActorContext.System(), Now);

        // No transition to Expired or anything a timer would pick.
        ((Action)(() => job.Expire(ActorContext.System(), Now))).Should().Throw<IllegalStateTransitionException>();

        job.Complete(4, 4, Kiosk, Now);
        job.Status.Should().Be(PrintJobStatus.Completed);
    }

    [Fact]
    public void A_printed_count_that_makes_no_sense_is_refused()
    {
        var job = Queued();
        job.StartPrinting(Kiosk, Now);

        ((Action)(() => job.Complete(1, 2, Kiosk, Now))).Should().Throw<DomainException>()
            .Which.Text.Key.Should().Be("printJobs.countsInvalid");
    }

    [Theory]
    [InlineData(null, 5, new[] { 1, 2, 3, 4, 5 })]
    [InlineData("", 3, new[] { 1, 2, 3 })]
    [InlineData("1-3,5,8-10", 10, new[] { 1, 2, 3, 5, 8, 9, 10 })]
    [InlineData(" 2 , 4-5 ", 5, new[] { 2, 4, 5 })]
    [InlineData("1-3,2", 5, new[] { 1, 2, 3 })]
    [InlineData("3,1", 5, new[] { 3, 1 })]
    public void A_page_range_selects_the_pages_it_names(string? range, int pageCount, int[] expected)
    {
        PageRange.TryParse(range, pageCount, out var pages).Should().BeTrue();
        pages.Should().Equal(expected);
    }

    [Theory]
    [InlineData("0", 5)]
    [InlineData("6", 5)]
    [InlineData("4-2", 5)]
    [InlineData("1,,2", 5)]
    [InlineData("a", 5)]
    [InlineData("1-", 5)]
    [InlineData("-1", 5)]
    [InlineData("1", 0)]
    public void A_range_that_does_not_fit_the_document_is_refused(string range, int pageCount)
        => PageRange.TryParse(range, pageCount, out _).Should().BeFalse();

    [Fact]
    public void Options_outside_the_plan_are_named()
    {
        var options = new PrintOptions(51, "9", false, false, "Letter", PageOrientation.Auto, 3, PageScaling.Fit);

        options.Problems(5).Select(problem => problem.Field)
            .Should().BeEquivalentTo(["copies", "paper", "pagesPerSheet", "pageRange"]);
    }
}
