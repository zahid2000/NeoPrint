using NeoPrint.Application.Analytics;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Application.Tests.Analytics;

/// <summary>
/// The pilot report's day figures (plan section 2.17, T-214): Baku days, the
/// way through the kiosk, payments, printing, supplies - and the same records
/// always giving the same days, which is what lets the rollup run again.
/// </summary>
public sealed class PilotDayBuilderTests
{
    private static readonly Guid Kiosk = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly May1 = new(2026, 5, 1);
    private static readonly DateTime Now = new(2026, 5, 3, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Baku is UTC+4 all year: 1 May in Baku runs from 30 April 20:00 to 1 May 20:00 UTC.</summary>
    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 5, day, hour, minute, 0, DateTimeKind.Utc);

    private static PilotSources Sources(
        IEnumerable<PilotEventRow>? events = null,
        IEnumerable<PilotJobRow>? jobs = null,
        IEnumerable<PilotPaymentRow>? payments = null,
        IEnumerable<PilotRefundRow>? refunds = null,
        IEnumerable<PilotAttemptRow>? attempts = null,
        IEnumerable<PilotCancelRow>? cancellations = null,
        IEnumerable<PilotSupplyRow>? supplies = null,
        IEnumerable<PilotLevelRow>? levels = null,
        IEnumerable<PilotIncidentRow>? incidents = null,
        IEnumerable<PilotFileRow>? files = null)
        => new(
            [.. events ?? []],
            [.. jobs ?? []],
            [.. payments ?? []],
            [.. refunds ?? []],
            [.. attempts ?? []],
            [.. cancellations ?? []],
            [.. supplies ?? []],
            [.. levels ?? []],
            [.. incidents ?? []],
            [.. files ?? []]);

    private static PilotEventRow Event(DateTime at, string session, string name, params (string Key, string Value)[] props)
        => new(at, Kiosk, session, name, props.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    /// <summary>A visit that gets as far as <paramref name="steps"/> on the way to an order with <paramref name="code"/>.</summary>
    private static IEnumerable<PilotEventRow> Visit(DateTime at, string session, int steps, string code = "")
    {
        yield return Event(at, session, "screen_view", ("screen", "Browse"));

        if (steps >= 2)
        {
            yield return Event(at.AddSeconds(10), session, "file_selected", ("format", "pdf"));
        }

        if (steps >= 3)
        {
            yield return Event(at.AddSeconds(20), session, "screen_view", ("screen", "Options"));
        }

        if (steps >= 4)
        {
            yield return Event(at.AddSeconds(30), session, "order_placed", ("jobCode", code), ("sheets", "1"));
        }
    }

    private static PilotDay Day(IReadOnlyList<PilotDay> days, DateOnly day, Guid? kiosk = null)
        => days.Single(d => d.Day == day.ToString("yyyy-MM-dd") && d.KioskId == (kiosk ?? Kiosk));

    [Fact]
    public void A_day_is_a_Baku_day_from_20_00_UTC_the_evening_before()
    {
        var sources = Sources(incidents:
        [
            new PilotIncidentRow(Kiosk, Utc(1, 19, 59), IncidentSeverity.High), // 1 May 23:59 in Baku
            new PilotIncidentRow(Kiosk, Utc(1, 20, 0), IncidentSeverity.Critical),         // 2 May 00:00 in Baku
            new PilotIncidentRow(Kiosk, new DateTime(2026, 4, 30, 20, 0, 0, DateTimeKind.Utc), IncidentSeverity.Medium), // 1 May 00:00
        ]);

        var days = PilotDayBuilder.Build(May1, May1.AddDays(1), [Kiosk], false, sources, Now);

        Day(days, May1).Incidents.Should().BeEquivalentTo([new NamedCount("High", 1), new NamedCount("Medium", 1)]);
        Day(days, May1.AddDays(1)).Incidents.Should().BeEquivalentTo([new NamedCount("Critical", 1)]);
        PilotDayBuilder.StartOf(May1).Should().Be(new DateTime(2026, 4, 30, 20, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Every_kiosk_and_date_has_a_day_even_an_empty_one_so_a_recount_to_nothing_replaces_the_old_figures()
    {
        var days = PilotDayBuilder.Build(May1, May1.AddDays(2), [Kiosk, Other], true, Sources(), Now);

        days.Should().HaveCount(9, "three dates for two kiosks and the app");
        days.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        days.Should().Contain(d => d.Id == "2026-05-01:app" && d.KioskId == null);
        days.Should().Contain(d => d.Id == $"2026-05-03:{Kiosk:N}");
        days.Should().OnlyContain(d => d.Visits == 0 && d.Funnel == PilotFunnel.Empty && d.Sales == 0);
    }

    [Fact]
    public void The_same_records_always_give_the_same_days()
    {
        var sources = Sources(
            events: Visit(Utc(1, 8), "s1", 4, "100001"),
            jobs: [new PilotJobRow(Guid.NewGuid(), Kiosk, "100001", Utc(1, 8), PrintJobStatus.Completed, Utc(1, 8, 1), false, Utc(1, 8, 2))],
            payments: [new PilotPaymentRow(Kiosk, Utc(1, 8), Utc(1, 8, 1), PaymentStatus.Succeeded, PaymentMethod.BankQr, null, 0.40m)]);

        var first = PilotDayBuilder.Build(May1, May1, [Kiosk], true, sources, Now);
        var again = PilotDayBuilder.Build(May1, May1, [Kiosk], true, sources, Now);

        again.Should().BeEquivalentTo(first, options => options.WithStrictOrdering());
        Day(first, May1).Sales.Should().Be(0.40m, "counted once, however many times it is worked out");
    }

    [Fact]
    public void The_funnel_follows_each_visit_to_its_order_and_reads_payment_and_printing_from_the_order()
    {
        var jobs = new[]
        {
            new PilotJobRow(Guid.NewGuid(), Kiosk, "200001", Utc(1, 9), PrintJobStatus.Completed, Utc(1, 9, 1), false, Utc(1, 9, 2)),
            new PilotJobRow(Guid.NewGuid(), Kiosk, "200002", Utc(1, 9), PrintJobStatus.Interrupted, Utc(1, 9, 1), false, Utc(1, 9, 2)),
            new PilotJobRow(Guid.NewGuid(), Kiosk, "200003", Utc(1, 9), PrintJobStatus.Expired, null, false, null),
            new PilotJobRow(Guid.NewGuid(), Kiosk, "200004", Utc(1, 9), PrintJobStatus.Queued, Utc(1, 9, 1), false, null),
        };
        var events = Visit(Utc(1, 9), "printed", 4, "200001")
            .Concat(Visit(Utc(1, 9), "jammed", 4, "200002"))
            .Concat(Visit(Utc(1, 9), "unpaid", 4, "200003"))
            .Concat(Visit(Utc(1, 9), "waiting", 4, "200004"))
            .Concat(Visit(Utc(1, 9), "options", 3))
            .Concat(Visit(Utc(1, 9), "preview", 2))
            .Concat(Visit(Utc(1, 9), "browsed", 1))
            .Append(Event(Utc(1, 9), "broken", "conversion_failed", ("format", "docx"), ("error", "pdf.damaged")))
            // A print result goes under the job, not under a visit of its own.
            .Append(Event(Utc(1, 9, 5), "job-200001", "print_completed", ("jobCode", "200001")))
            // A visit that began the evening before belongs to that day.
            .Concat(Visit(new DateTime(2026, 4, 30, 19, 50, 0, DateTimeKind.Utc), "yesterday", 4, "200001"));

        var day = Day(PilotDayBuilder.Build(May1, May1, [Kiosk], false, Sources(events: events, jobs: jobs), Now), May1);

        day.Visits.Should().Be(8);
        day.Funnel.Should().Be(new PilotFunnel(Selected: 7, Previewed: 6, Options: 5, PaymentScreen: 4, Paid: 3, Printing: 2, Printed: 1));
        day.Formats.Should().BeEquivalentTo([new FormatCount("pdf", 6, 0), new FormatCount("docx", 1, 1)]);
        day.ConversionErrors.Should().BeEquivalentTo([new NamedCount("pdf.damaged", 1)]);
        day.Orders.Should().Be(4, "orders count by the job, on the day it was made");
    }

    [Fact]
    public void Payments_count_on_the_day_they_began_with_the_banks_reason_and_sales_on_the_day_they_were_paid()
    {
        var payments = new[]
        {
            new PilotPaymentRow(Kiosk, Utc(1, 10), Utc(1, 10, 1), PaymentStatus.Succeeded, PaymentMethod.BankQr, null, 0.40m),
            new PilotPaymentRow(Kiosk, Utc(1, 10), null, PaymentStatus.Failed, PaymentMethod.BankQr, "payments.declined", 0.40m),
            new PilotPaymentRow(Kiosk, Utc(1, 10), null, PaymentStatus.Failed, PaymentMethod.BankQr, null, 0.40m),
            new PilotPaymentRow(Kiosk, Utc(1, 10), null, PaymentStatus.Expired, PaymentMethod.MobileApp, null, 0.40m),
            new PilotPaymentRow(Kiosk, Utc(1, 10), Utc(1, 10, 2), PaymentStatus.Refunded, PaymentMethod.MobileApp, null, 1.20m),
            // Started on the 1st (Baku), paid after midnight: sold on the 2nd.
            new PilotPaymentRow(Kiosk, Utc(1, 19, 58), Utc(1, 20, 1), PaymentStatus.Succeeded, PaymentMethod.BankQr, null, 0.80m),
            new PilotPaymentRow(Kiosk, Utc(1, 10), null, PaymentStatus.Pending, PaymentMethod.BankQr, null, 0.40m),
        };
        var refunds = new[] { new PilotRefundRow(Kiosk, Utc(1, 11), 1.20m, "SystemError") };

        var days = PilotDayBuilder.Build(May1, May1.AddDays(1), [Kiosk], false, Sources(payments: payments, refunds: refunds), Now);
        var day = Day(days, May1);

        day.PaymentsStarted.Should().Be(7);
        day.PaymentsSucceeded.Should().Be(3, "a payment later refunded was still paid");
        day.PaymentFailures.Should().BeEquivalentTo(
            [new NamedCount(PilotDayBuilder.DeclinedKey, 2), new NamedCount(PilotDayBuilder.ExpiredKey, 1)],
            "a refusal with no reason kept is still the bank's refusal; a pending one is not a failure");
        day.Methods.Should().BeEquivalentTo([new MethodCount("BankQr", 5, 2), new MethodCount("MobileApp", 2, 1)]);
        day.Sales.Should().Be(1.60m);
        day.Refunds.Should().Be(1);
        day.Refunded.Should().Be(1.20m);
        day.RefundReasons.Should().BeEquivalentTo([new NamedCount("SystemError", 1)]);
        Day(days, May1.AddDays(1)).Sales.Should().Be(0.80m);
    }

    [Fact]
    public void Printing_paper_toner_cancellations_and_incidents_count_for_their_kiosk_only()
    {
        var sources = Sources(
            attempts:
            [
                new PilotAttemptRow(Kiosk, Utc(1, 12, 2), "Completed", null, Utc(1, 12), Utc(1, 12, 2), 3),
                new PilotAttemptRow(Kiosk, Utc(1, 13, 1), "Completed", null, Utc(1, 13), Utc(1, 13, 1), 1),
                new PilotAttemptRow(Kiosk, Utc(1, 14), "Completed", null, null, null, 1),
                new PilotAttemptRow(Kiosk, Utc(1, 15), "Interrupted", "printer.paperJam", Utc(1, 15), Utc(1, 15), 0),
                new PilotAttemptRow(Kiosk, Utc(1, 16), "Interrupted", null, null, null, 0),
                new PilotAttemptRow(Other, Utc(1, 16), "Interrupted", "printer.offline", null, null, 0),
            ],
            cancellations: [new PilotCancelRow(Kiosk, Utc(1, 9)), new PilotCancelRow(Other, Utc(1, 9))],
            supplies:
            [
                new PilotSupplyRow(Kiosk, Utc(1, 12), "PaperA4", SupplyEventType.JobPrinted, -3),
                new PilotSupplyRow(Kiosk, Utc(1, 13), "PaperA4", SupplyEventType.JobPrinted, -1),
                new PilotSupplyRow(Kiosk, Utc(1, 13), "PaperA3", SupplyEventType.JobPrinted, -2),
                new PilotSupplyRow(Kiosk, Utc(1, 13), "PaperA4", SupplyEventType.TestPrint, -1),
                new PilotSupplyRow(Kiosk, Utc(1, 14), "PaperA4", SupplyEventType.Refill, 400),
                new PilotSupplyRow(Kiosk, Utc(1, 14), "Toner:Black", SupplyEventType.Replaced, 0),
            ],
            levels:
            [
                // The reading before the day counts as where the day began.
                new PilotLevelRow(Kiosk, new DateTime(2026, 4, 30, 18, 0, 0, DateTimeKind.Utc), "Black Toner", 50),
                new PilotLevelRow(Kiosk, Utc(1, 10), "Black Toner", 48),
                new PilotLevelRow(Kiosk, Utc(1, 11), "Black Toner", 45),
                new PilotLevelRow(Kiosk, Utc(1, 12), "Black Toner", 100), // a new cartridge: not a use
                new PilotLevelRow(Kiosk, Utc(1, 13), "Black Toner", 99),
            ]);

        var day = Day(PilotDayBuilder.Build(May1, May1, [Kiosk, Other], false, sources, Now), May1);

        day.PrintsSucceeded.Should().Be(3);
        day.PrintsTimed.Should().Be(2, "a print without both times is not measured");
        day.PrintSeconds.Should().Be(180);
        day.PrintsFailed.Should().Be(2);
        day.PrintErrors.Should().BeEquivalentTo([new NamedCount("printer.error", 1), new NamedCount("printer.paperJam", 1)]);
        day.Cancelled.Should().Be(1);
        day.Paper.Should().BeEquivalentTo([new NamedCount("A4", 4), new NamedCount("A3", 2)]);
        day.TestSheets.Should().Be(1);
        day.Toner.Should().BeEquivalentTo([new NamedCount("Black Toner", 6)]);
        day.TonerReplaced.Should().Be(1);
    }

    [Fact]
    public void The_apps_uploads_have_a_day_of_their_own_and_only_when_asked()
    {
        var files = new[]
        {
            new PilotFileRow(Utc(1, 8), "PDF", false, null),
            new PilotFileRow(Utc(1, 8), "jpg", false, null),
            new PilotFileRow(Utc(1, 8), "docx", true, "conversion.timeout"),
        };

        var withApp = PilotDayBuilder.Build(May1, May1, [Kiosk], true, Sources(files: files), Now);
        var app = withApp.Single(d => d.KioskId is null);

        app.Formats.Should().BeEquivalentTo([new FormatCount("pdf", 1, 0), new FormatCount("jpg", 1, 0), new FormatCount("docx", 1, 1)]);
        app.ConversionErrors.Should().BeEquivalentTo([new NamedCount("conversion.timeout", 1)]);
        Day(withApp, May1).Formats.Should().BeEmpty();

        PilotDayBuilder.Build(May1, May1, [Kiosk], false, Sources(files: files), Now).Should().OnlyContain(d => d.KioskId != null);
    }

    [Fact]
    public void Abandoned_visits_are_counted_by_the_screen_they_ended_on()
    {
        var events = new[]
        {
            Event(Utc(1, 8), "a", "flow_abandoned", ("lastScreen", "QrPayment"), ("seconds", "190")),
            Event(Utc(1, 8), "b", "flow_abandoned", ("lastScreen", "QrPayment"), ("seconds", "30")),
            Event(Utc(1, 8), "c", "flow_abandoned", ("lastScreen", "Preview"), ("seconds", "12")),
        };

        Day(PilotDayBuilder.Build(May1, May1, [Kiosk], false, Sources(events: events), Now), May1)
            .Abandoned.Should().Equal(new NamedCount("QrPayment", 2), new NamedCount("Preview", 1));
    }
}
