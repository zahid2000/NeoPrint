using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Incidents;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A kiosk that stops (plan section 9.6). Scenario A - the printer is down
/// and the kiosk says so: the kiosk is Paused, the guests' bank payments go
/// back by themselves, the registered owners are offered the choice, and the
/// work goes on when the printer returns. Scenario B - the kiosk goes quiet:
/// Offline and an incident, nothing cancelled and nothing refunded.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskOutageTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskOutageTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // Scenario A: the heartbeat says the printer is not ready
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_printer_that_is_not_ready_for_a_moment_stops_nothing()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-blip");
        var (jobId, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);

        await BeatAsync(kiosk, printerOnline: false, problem: "printer.offline");

        var stored = await KioskAsync(kiosk.KioskId);
        stored.Status.Should().Be(KioskStatus.Online, "one bad reading is not an outage");
        stored.PrinterDownSince.Should().NotBeNull();
        stored.FaultSince.Should().BeNull();
        (await IncidentsAsync(kiosk.KioskId)).Should().BeEmpty();

        await BeatAsync(kiosk, printerOnline: true);

        (await KioskAsync(kiosk.KioskId)).PrinterDownSince.Should().BeNull();
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Queued, "the guest's order was never in danger");
        (await RefundsAsync(paymentId)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_printer_that_stays_down_pauses_the_kiosk_refunds_the_guests_and_offers_the_owners_a_choice()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-down");
        var (guestJob, guestPayment, guestPrice) = await PaidBankJobAsync(_fixture, kiosk);
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        var (ownerJob, _) = await RegisteredJobAsync(kiosk, owner);

        await StopPrinterAsync(kiosk, "printer.outOfPaper");

        // The kiosk, and why.
        var stopped = await KioskAsync(kiosk.KioskId);
        stopped.Status.Should().Be(KioskStatus.Paused);
        stopped.FaultCode.Should().Be("printer.outOfPaper");

        var incident = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        incident.Type.Should().Be(IncidentType.PaperOut);
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        incident.Open.Should().BeTrue();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/incidents?kioskId={kiosk.KioskId}&status=Open");
        var row = listed.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        row.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        row.GetProperty("details").GetString().Should().Contain(kiosk.KioskCode).And.Contain("Printerdə kağız bitib", "the reader is told what is wrong, in words");

        // The guest's bank payment goes back by itself, in full: nothing printed.
        await EventuallyAsync(() => JobAsync(guestJob), job => job.Status == PrintJobStatus.Cancelled, "the guest's order being cancelled");
        var refund = (await EventuallyAsync(() => RefundsAsync(guestPayment), refunds => refunds.Count > 0, "the guest's refund"))
            .Should().ContainSingle().Subject;
        refund.Amount.Amount.Should().Be(guestPrice);
        refund.Reason.Should().Be(RefundReason.SystemError);

        // The owner's order waits, and the owner is asked.
        (await JobAsync(ownerJob)).Status.Should().Be(PrintJobStatus.Queued, "a registered user decides for themselves");

        var offers = await EventuallyAsync(
            () => NotificationsAsync(owner.Id), all => all.Any(n => n.Type == NotificationType.KioskOutOfServiceRefundOffer), "the refund offer");
        offers.Where(n => n.Type is NotificationType.KioskOutOfServiceRefundOffer or NotificationType.JobInterruptedRefundOffer)
            .Should().ContainSingle("one order, one offer");

        var mine = (await owner.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my"))
            .EnumerateArray().Single(job => job.GetProperty("id").GetGuid() == ownerJob);
        mine.GetProperty("canCancel").GetBoolean().Should().BeTrue();
        mine.GetProperty("kioskStopped").GetBoolean().Should().BeTrue();

        // No new orders while it lasts.
        using var order = NewOrder();
        (await kiosk.Client.SendAsync(order)).StatusCode.Should().NotBe(HttpStatusCode.Created, "a paused kiosk takes no orders");

        // The same report again begins nothing.
        await BeatAsync(kiosk, printerOnline: false, problem: "printer.outOfPaper");
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle();

        // The printer returns: the kiosk is back, the incident closes itself, the owner's order is still there.
        await BeatAsync(kiosk, printerOnline: true);

        var back = await KioskAsync(kiosk.KioskId);
        back.Status.Should().Be(KioskStatus.Online);
        back.FaultSince.Should().BeNull();

        var closed = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        closed.Status.Should().Be(IncidentStatus.Resolved);
        closed.ResolvedBy.Should().BeNull("the system closed it");
        UserMessage.Decode(closed.Resolution!).Key.Should().Be("incidents.resolution.printerRecovered");

        (await JobAsync(ownerJob)).Status.Should().Be(PrintJobStatus.Queued);

        using var again = NewOrder();
        (await kiosk.Client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [DockerFact]
    public async Task A_different_problem_during_the_same_outage_gets_its_own_incident_and_refunds_nobody_twice()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-two");

        await StopPrinterAsync(kiosk, "printer.offline");
        await BeatAsync(kiosk, printerOnline: false, problem: "printer.paperJam");

        var open = await IncidentsAsync(kiosk.KioskId);
        open.Select(incident => incident.Type).Should().BeEquivalentTo([IncidentType.PrinterOffline, IncidentType.PaperJam]);
        (await KioskAsync(kiosk.KioskId)).FaultCode.Should().Be("printer.paperJam");

        // A key the kiosk made up is not stored: it reads as the general fault.
        await BeatAsync(kiosk, printerOnline: false, problem: "<script>alert(1)</script>");
        (await KioskAsync(kiosk.KioskId)).FaultCode.Should().Be("printer.error");

        await BeatAsync(kiosk, printerOnline: true);

        (await IncidentsAsync(kiosk.KioskId)).Should().OnlyContain(incident => incident.Status == IncidentStatus.Resolved);
    }

    // -----------------------------------------------------------------------
    // Scenario A: a job stops at the printer
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_job_the_printer_stopped_pauses_the_kiosk_at_once_and_prints_again_when_the_printer_is_back()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-job");
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        var (jobId, _) = await RegisteredJobAsync(kiosk, owner);

        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt(1));
        await ReportAsync(kiosk.Client, jobId, "Interrupted", sides: 1, sheets: 1, errorCode: "printer.paperJam", attempt: Attempt(1));

        // No waiting for heartbeats: a job that failed on the printer is no blip.
        var stopped = await EventuallyAsync(() => KioskAsync(kiosk.KioskId), k => k.Status == KioskStatus.Paused, "the kiosk being paused");
        stopped.FaultCode.Should().Be("printer.paperJam");
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Which.Type.Should().Be(IncidentType.PaperJam);

        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Interrupted, "a registered user's order is not cancelled for them");
        await EventuallyAsync(
            () => NotificationsAsync(owner.Id), all => all.Any(n => n.Type == NotificationType.JobInterruptedRefundOffer), "the refund offer");

        await BeatAsync(kiosk, printerOnline: true);

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);

        var resumed = await JobAsync(jobId);
        resumed.Status.Should().Be(PrintJobStatus.Queued, "the work goes on when the printer returns");
        resumed.ErrorReason.Should().BeNull();

        var queue = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue");
        queue.ToString().Should().Contain(jobId.ToString(), "the kiosk is handed the order again");

        // And it prints.
        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt(2));
        await ReportAsync(kiosk.Client, jobId, "Completed", sides: 3, sheets: 3, attempt: Attempt(2));
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Completed);

        // Both runs used paper, and both came off the counter: one sheet, then three.
        using var scope = _fixture.Services.CreateScope();
        var used = await scope.ServiceProvider.GetRequiredService<MongoContext>().SupplyEvents
            .Find(movement => movement.JobId == jobId).SortBy(movement => movement.At).ToListAsync();
        used.Select(movement => movement.Delta).Should().Equal(-1, -3);
        (await KioskAsync(kiosk.KioskId)).PaperStock["A4"].Current.Should().Be(500 - 4);
    }

    [DockerFact]
    public async Task An_order_that_stopped_three_times_is_not_sent_to_the_printer_a_fourth_time()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-thrice");
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        var (jobId, _) = await RegisteredJobAsync(kiosk, owner);

        for (var attempt = 1; attempt <= KioskFaultHandlers.MaxAutomaticAttempts; attempt++)
        {
            await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt(attempt));
            await ReportAsync(kiosk.Client, jobId, "Interrupted", errorCode: "printer.paperJam", attempt: Attempt(attempt));
            await EventuallyAsync(() => KioskAsync(kiosk.KioskId), k => k.Status == KioskStatus.Paused, $"the kiosk being paused (attempt {attempt})");

            await BeatAsync(kiosk, printerOnline: true);

            (await JobAsync(jobId)).Status.Should().Be(
                attempt < KioskFaultHandlers.MaxAutomaticAttempts ? PrintJobStatus.Queued : PrintJobStatus.Interrupted);
        }

        var asked = (await IncidentsAsync(kiosk.KioskId)).Where(incident => incident.Open).Should().ContainSingle().Subject;
        asked.Type.Should().Be(IncidentType.Other);
        asked.Severity.Should().Be(IncidentSeverity.High);
        asked.EntityId.Should().Be(jobId, "somebody is asked to look at this order");

        // Its owner can still take the money back.
        (await owner.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task A_file_that_could_not_be_prepared_stops_one_order_not_the_kiosk()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-file");
        var (jobId, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);

        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt(1));
        await ReportAsync(kiosk.Client, jobId, "Interrupted", errorCode: "printer.spoolFailed", attempt: Attempt(1));

        // The guest is refunded as before...
        await EventuallyAsync(() => RefundsAsync(paymentId), refunds => refunds.Count > 0, "the refund");

        // ...and the kiosk stays in service, also once every consumer of the interruption has had its turn.
        await Task.Delay(1500);
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);
        (await IncidentsAsync(kiosk.KioskId)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // The owner's choice
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_owner_cancels_a_waiting_order_and_the_money_goes_back_to_the_wallet()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-cancel");
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        var (jobId, price) = await RegisteredJobAsync(kiosk, owner);

        (await WalletAsync(owner)).Should().Be(5m - price);

        // Somebody else's order is not there at all.
        (await stranger.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Queued);

        (await owner.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var cancelled = await JobAsync(jobId);
        cancelled.Status.Should().Be(PrintJobStatus.Cancelled);
        UserMessage.Decode(cancelled.CancelReason!).Key.Should().Be("printJobs.reason.cancelledByOwner");

        await EventuallyAsync(() => WalletAsync(owner), balance => balance == 5m, "the money coming back to the wallet");

        // Pressed twice: nothing more happens, and nothing more comes back.
        (await owner.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Task.Delay(1500);
        (await WalletAsync(owner)).Should().Be(5m);

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).ToString()
            .Should().NotContain(jobId.ToString(), "the kiosk no longer has it to print");
    }

    [DockerFact]
    public async Task An_order_at_the_printer_or_one_that_has_finished_cannot_be_cancelled_by_its_owner()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-late");
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        var (jobId, price) = await RegisteredJobAsync(kiosk, owner);

        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt(1));

        (await owner.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await ReportAsync(kiosk.Client, jobId, "Completed", sides: 3, sheets: 3, attempt: Attempt(1));

        (await owner.Client.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Completed);
        (await WalletAsync(owner)).Should().Be(5m - price, "a printed order is paid for");

        // And without signing in there is nothing to cancel.
        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PostAsync($"/api/print-jobs/my/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------
    // Scenario B: the kiosk goes quiet
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_kiosk_that_stops_calling_in_goes_offline_with_an_incident_and_comes_back_by_itself()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-silent");
        var (jobId, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);

        await BeatAsync(kiosk, printerOnline: true);

        // Not silent yet: nothing happens.
        (await SendAsync(new MarkKioskOfflineCommand(kiosk.KioskId))).Should().BeFalse();
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);

        await LastHeardAsync(kiosk.KioskId, DateTime.UtcNow.AddMinutes(-5));

        (await SendAsync(new MarkKioskOfflineCommand(kiosk.KioskId))).Should().BeTrue();

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Offline);

        var incident = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        incident.Type.Should().Be(IncidentType.ConnectionLost);
        incident.Severity.Should().Be(IncidentSeverity.Critical);

        // Scenario B: nothing is known, so nothing is cancelled and nothing refunded.
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Queued);
        (await RefundsAsync(paymentId)).Should().BeEmpty();

        // The monitor's next run finds it dealt with.
        (await SendAsync(new MarkKioskOfflineCommand(kiosk.KioskId))).Should().BeFalse();
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle();

        await BeatAsync(kiosk, printerOnline: true);

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);

        var closed = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        closed.Status.Should().Be(IncidentStatus.Resolved);
        UserMessage.Decode(closed.Resolution!).Key.Should().Be("incidents.resolution.heartbeatReturned");
    }

    [DockerFact]
    public async Task A_paused_kiosk_that_goes_quiet_keeps_its_pause_and_still_gets_the_incident()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-quiet-pause");
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        await BeatAsync(kiosk, printerOnline: true);
        (await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await LastHeardAsync(kiosk.KioskId, DateTime.UtcNow.AddMinutes(-5));

        (await SendAsync(new MarkKioskOfflineCommand(kiosk.KioskId))).Should().BeTrue();

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Maintenance, "the pause is the administrator's, whatever the connection does");
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Which.Type.Should().Be(IncidentType.ConnectionLost);

        await BeatAsync(kiosk, printerOnline: true);

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Maintenance);
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Which.Status.Should().Be(IncidentStatus.Resolved);
    }

    // -----------------------------------------------------------------------
    // POST incidents: what the kiosk reports itself
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_kiosk_reports_a_crash_or_a_printer_fault_and_nothing_it_was_not_asked_for()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-report");

        (await ReportIncidentAsync(kiosk.Client, "AppCrash")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReportIncidentAsync(kiosk.Client, "AppCrash")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var crash = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle("however often it crashes, the list holds one").Subject;
        crash.Type.Should().Be(IncidentType.AppCrash);
        crash.Severity.Should().Be(IncidentSeverity.High);
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online, "the application came back");

        // A printer fault said outright: no waiting for the heartbeats to agree.
        (await ReportIncidentAsync(kiosk.Client, "PaperJam")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Paused);

        // Types that are the backend's own to conclude, or no type at all.
        foreach (var type in new[] { "RefundFailed", "DiskLow", "Other", "NoSuchThing", "" })
        {
            (await ReportIncidentAsync(kiosk.Client, type)).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"\"{type}\" is not the kiosk's to report");
        }

        (await IncidentsAsync(kiosk.KioskId)).Should().HaveCount(2);

        // Only a kiosk reports.
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var anonymous = _fixture.CreateApiClient();

        (await ReportIncidentAsync(user.Client, "AppCrash")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReportIncidentAsync(admin, "AppCrash")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReportIncidentAsync(anonymous, "AppCrash")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_kiosk_left_without_its_printer_is_paused_and_one_with_no_printer_at_all_gets_an_incident()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-gone");

        // The chosen printer is not in the list any more (plan section 7.7).
        (await kiosk.Client.PutAsJsonAsync("/api/kiosks/me/printers", new
        {
            printers = new[] { new { name = "Another printer", driverName = "Test driver", isOnline = true, color = false, duplex = false, a3 = false } },
        })).EnsureSuccessStatusCode();

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Paused);
        var missing = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        missing.Type.Should().Be(IncidentType.SelectedPrinterMissing);
        missing.Severity.Should().Be(IncidentSeverity.Critical);

        // No printer at all.
        (await kiosk.Client.PutAsJsonAsync("/api/kiosks/me/printers", new { printers = Array.Empty<object>() })).EnsureSuccessStatusCode();

        var none = (await IncidentsAsync(kiosk.KioskId)).Single(incident => incident.Type == IncidentType.NoPrinterDetected);
        none.Severity.Should().Be(IncidentSeverity.High);
        none.Open.Should().BeTrue();

        // It is back: the list has it, and the heartbeat says it is ready.
        (await kiosk.Client.PutAsJsonAsync("/api/kiosks/me/printers", new
        {
            printers = new[] { new { name = "Test printer", driverName = "Test driver", isOnline = true, color = true, duplex = true, a3 = false } },
        })).EnsureSuccessStatusCode();
        await BeatAsync(kiosk, printerOnline: true);

        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);
        (await IncidentsAsync(kiosk.KioskId)).Should().OnlyContain(incident => incident.Status == IncidentStatus.Resolved);
    }

    [DockerFact]
    public async Task A_kiosk_in_safe_mode_has_a_critical_incident_until_it_leaves()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-safe");

        await BeatAsync(kiosk, printerOnline: true, safeMode: true);
        await BeatAsync(kiosk, printerOnline: true, safeMode: true);

        var incident = (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        incident.Type.Should().Be(IncidentType.AppCrash);
        incident.Severity.Should().Be(IncidentSeverity.Critical);

        await BeatAsync(kiosk, printerOnline: true, safeMode: false);

        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle().Which.Status.Should().Be(IncidentStatus.Resolved);
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Online);
    }

    // -----------------------------------------------------------------------
    // The panel
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task An_incident_shows_the_orders_it_touched_and_is_acknowledged_and_resolved_with_a_note_in_the_audit_log()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-panel");
        var (guestJob, guestPayment, guestPrice) = await PaidBankJobAsync(_fixture, kiosk);
        using var owner = await TestUsers.ConfirmedAsync(_fixture);
        var (ownerJob, _) = await RegisteredJobAsync(kiosk, owner);

        await StopPrinterAsync(kiosk, "printer.offline");
        await EventuallyAsync(() => RefundsAsync(guestPayment), refunds => refunds.Count > 0, "the guest's refund");

        var incidentId = (await IncidentsAsync(kiosk.KioskId)).Single().Id;
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/me")).GetProperty("id").GetGuid();

        // Plan section 9.6, A.6: the payments it touched and where their refunds stand.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/incidents/{incidentId}");
        detail.GetProperty("incident").GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);

        var affected = detail.GetProperty("affected").EnumerateArray().ToList();
        affected.Select(order => order.GetProperty("jobId").GetGuid()).Should().BeEquivalentTo([guestJob, ownerJob]);

        var guest = affected.Single(order => order.GetProperty("jobId").GetGuid() == guestJob);
        guest.GetProperty("registered").GetBoolean().Should().BeFalse();
        guest.GetProperty("status").GetString().Should().Be("Cancelled");
        guest.GetProperty("paymentId").GetGuid().Should().Be(guestPayment);
        guest.GetProperty("paid").GetDecimal().Should().Be(guestPrice);
        guest.GetProperty("refunds").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("amount").GetDecimal().Should().Be(guestPrice);

        var waiting = affected.Single(order => order.GetProperty("jobId").GetGuid() == ownerJob);
        waiting.GetProperty("registered").GetBoolean().Should().BeTrue();
        waiting.GetProperty("status").GetString().Should().Be("Queued");
        waiting.GetProperty("refunds").GetArrayLength().Should().Be(0, "the owner has not decided");

        // Acknowledge: once, however often it is pressed.
        var acknowledged = await admin.PostAsync($"/api/admin/incidents/{incidentId}/acknowledge", null);
        acknowledged.StatusCode.Should().Be(HttpStatusCode.OK, await acknowledged.Content.ReadAsStringAsync());
        (await acknowledged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Acknowledged");
        (await admin.PostAsync($"/api/admin/incidents/{incidentId}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Resolve: not without saying what was done.
        (await admin.PostAsJsonAsync($"/api/admin/incidents/{incidentId}/resolve", new { note = "  " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/admin/incidents/{incidentId}/resolve", new { note = new string('x', 501) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IncidentsAsync(kiosk.KioskId)).Single().Status.Should().Be(IncidentStatus.Acknowledged);

        var resolved = await admin.PostAsJsonAsync($"/api/admin/incidents/{incidentId}/resolve", new { note = "Kabel dəyişdirildi" });
        resolved.StatusCode.Should().Be(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());

        var view = await resolved.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("status").GetString().Should().Be("Resolved");
        view.GetProperty("resolution").GetString().Should().Be("Qeyd: Kabel dəyişdirildi");
        view.GetProperty("resolvedBy").GetGuid().Should().Be(me);

        using var scope = _fixture.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(row => row.EntityId == incidentId.ToString())
            .SortBy(row => row.CreatedAt)
            .ToListAsync();

        audit.Select(row => row.Action).Should().Equal("incidents.acknowledge", "incidents.resolve");
        audit.Should().OnlyContain(row => row.AdminUserId == me && row.EntityType == "Incident");
        audit[1].Reason.Should().Be("Kabel dəyişdirildi");

        // Closing the incident by hand does not start the printer: the kiosk waits for it.
        (await KioskAsync(kiosk.KioskId)).Status.Should().Be(KioskStatus.Paused);
    }

    [DockerFact]
    public async Task An_account_with_a_kiosk_scope_sees_and_touches_only_its_own_kiosks_incidents()
    {
        if (Skip) { return; }

        using var mine = await ReadyKioskAsync(_fixture, "outage-scope-in");
        using var theirs = await ReadyKioskAsync(_fixture, "outage-scope-out");

        await StopPrinterAsync(mine, "printer.paperJam");
        await StopPrinterAsync(theirs, "printer.paperJam");

        var myIncident = (await IncidentsAsync(mine.KioskId)).Single().Id;
        var theirIncident = (await IncidentsAsync(theirs.KioskId)).Single().Id;

        using var scoped = await ScopedAdminAsync(
            KioskScope.Only(mine.KioskId), Permissions.IncidentsView, Permissions.IncidentsAcknowledge, Permissions.IncidentsResolve);

        var list = (await scoped.GetFromJsonAsync<JsonElement>("/api/admin/incidents?pageSize=100")).GetProperty("items").EnumerateArray().ToList();
        list.Should().ContainSingle().Which.GetProperty("id").GetGuid().Should().Be(myIncident, "nothing of another kiosk, and nothing that is about no kiosk");

        (await scoped.GetFromJsonAsync<JsonElement>($"/api/admin/incidents?kioskId={theirs.KioskId}"))
            .GetProperty("total").GetInt64().Should().Be(0);

        (await scoped.GetAsync($"/api/admin/incidents/{theirIncident}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.PostAsync($"/api/admin/incidents/{theirIncident}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.PostAsJsonAsync($"/api/admin/incidents/{theirIncident}/resolve", new { note = "not mine" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await IncidentsAsync(theirs.KioskId)).Single().Status.Should().Be(IncidentStatus.Open);

        (await scoped.PostAsync($"/api/admin/incidents/{myIncident}/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Seeing is not the same as acting.
        using var viewer = await ScopedAdminAsync(KioskScope.Only(mine.KioskId), Permissions.IncidentsView);
        (await viewer.GetAsync($"/api/admin/incidents/{myIncident}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync($"/api/admin/incidents/{myIncident}/resolve", new { note = "no right" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // E-mail and Telegram
    // -----------------------------------------------------------------------

    private sealed class CapturedTelegram : ITelegramSender
    {
        public List<(string ChatId, string Text)> Sent { get; } = [];

        public bool Enabled { get; set; } = true;

        public bool Fail { get; set; }

        public Task SendAsync(string chatId, string text, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new HttpRequestException("Telegram answered 502.");
            }

            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }
    }

    [DockerFact]
    public async Task An_urgent_incident_goes_by_email_and_telegram_to_those_who_look_after_that_kiosk_once()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "outage-mail");
        using var other = await ReadyKioskAsync(_fixture, "outage-mail-other");

        var keeper = await AdminAccountAsync(KioskScope.Only(kiosk.KioskId), "100200300", Permissions.IncidentsView);
        var elsewhere = await AdminAccountAsync(KioskScope.Only(other.KioskId), "999", Permissions.IncidentsView);
        var noRight = await AdminAccountAsync(KioskScope.Only(kiosk.KioskId), "998", Permissions.KiosksView);

        // Written straight to the collection: no event, so only this test's host sends anything.
        var incident = Incident.Raise(
            IncidentType.PaperJam,
            IncidentSeverity.Critical,
            UserMessage.Of("incidents.kiosk.printerFault", ("kiosk", kiosk.KioskCode), ("printer", "<b>HP</b> & Co"), ("problem", "paperJam")),
            $"test-mail:{Guid.NewGuid():N}",
            DateTime.UtcNow,
            kiosk.KioskId);
        var quiet = Incident.Raise(
            IncidentType.DiskLow, IncidentSeverity.Medium, UserMessage.Of("incidents.kiosk.appCrash", ("kiosk", kiosk.KioskCode)), $"test-mail:{Guid.NewGuid():N}", DateTime.UtcNow, kiosk.KioskId);

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents.InsertManyAsync([incident, quiet]);
        }

        var telegram = new CapturedTelegram();
        await using var host = _fixture.WithHttpOnlyHost(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITelegramSender>();
            services.AddSingleton<ITelegramSender>(telegram);
        }));
        var emails = host.Services.GetRequiredService<CapturedEmails>();

        async Task<int> NotifyAsync(Guid id)
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NotifyIncidentCommand(id));
        }

        // Telegram down: the e-mails go, the event is asked for again.
        telegram.Fail = true;
        await ((Func<Task>)(() => NotifyAsync(incident.Id))).Should().ThrowAsync<InvalidOperationException>();

        var mail = emails.Latest(keeper.Email);
        mail.Should().NotBeNull();
        mail!.Subject.Should().Contain("Critical").And.Contain(kiosk.KioskCode);
        mail.HtmlBody.Should().Contain("&lt;b&gt;HP&lt;/b&gt; &amp; Co", "what a kiosk reported is text, never markup")
            .And.NotContain("<b>HP</b>");
        emails.Latest(elsewhere.Email).Should().BeNull("not their kiosk");
        emails.Latest(noRight.Email).Should().BeNull("no right to see incidents");
        emails.Latest(NeoPrintApiFixture.SeedAdminEmail).Should().NotBeNull("an account that sees every kiosk is told");

        var sentSoFar = emails.Sent.Count;

        // Delivered again: nobody gets the e-mail twice, Telegram gets through now.
        telegram.Fail = false;
        (await NotifyAsync(incident.Id)).Should().BeGreaterThan(0);

        emails.Sent.Should().HaveCount(sentSoFar);
        telegram.Sent.Should().Contain(message => message.ChatId == "100200300");
        telegram.Sent.Should().NotContain(message => message.ChatId == "999" || message.ChatId == "998");
        telegram.Sent.Single(message => message.ChatId == "100200300").Text
            .Should().Contain(kiosk.KioskCode).And.Contain("Critical").And.Contain("<b>HP</b> & Co", "plain text is sent as it is");

        // And a third time: nothing at all.
        var telegrams = telegram.Sent.Count;
        (await NotifyAsync(incident.Id)).Should().Be(0);
        telegram.Sent.Should().HaveCount(telegrams);

        // What is kept is who was told, never where.
        using (var scope = _fixture.Services.CreateScope())
        {
            var marks = await scope.ServiceProvider.GetRequiredService<MongoContext>().IncidentNotifications
                .Find(mark => mark.IncidentId == incident.Id).Project(mark => mark.Id).ToListAsync();

            marks.Should().Contain(IncidentNotification.IdOf(incident.Id, "email", keeper.Id))
                .And.Contain(IncidentNotification.IdOf(incident.Id, "telegram", keeper.Id));
            marks.Should().NotContain(mark => mark.Contains('@') || mark.Contains("100200300"));
        }

        // Medium stays in the panel.
        (await NotifyAsync(quiet.Id)).Should().Be(0);
        emails.Sent.Should().HaveCount(sentSoFar);
    }

    private sealed class CapturedLog : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Line(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Line(CapturedLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Lines.Enqueue($"{category} {formatter(state, exception)} {exception}");
        }
    }

    [DockerFact]
    public async Task The_telegram_token_is_in_no_log_line_and_in_no_error()
    {
        if (Skip) { return; }

        const string token = "424242:test-bot-token-that-must-stay-secret";
        var log = new CapturedLog();

        await using var host = _fixture.WithHttpOnlyHost(builder =>
        {
            builder.UseSetting(TelegramSender.TokenKey, token);
            // Nothing listens there: the request is made and fails.
            builder.UseSetting(TelegramSender.ApiUrlKey, "http://127.0.0.1:9");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        });

        var sender = host.Services.GetRequiredService<ITelegramSender>();
        sender.Enabled.Should().BeTrue();

        var failure = await ((Func<Task>)(() => sender.SendAsync("100200300", "salam"))).Should().ThrowAsync<HttpRequestException>();

        failure.Which.ToString().Should().NotContain(token).And.NotContain("test-bot-token");
        log.Lines.Should().NotContain(line => line.Contains("test-bot-token"), "the token is part of the address, and the address must not be logged");

        // Without a token the channel is off and silent.
        await using var off = _fixture.WithHttpOnlyHost(builder => builder.UseSetting(TelegramSender.TokenKey, string.Empty));
        var disabled = off.Services.GetRequiredService<ITelegramSender>();
        disabled.Enabled.Should().BeFalse();
        await disabled.SendAsync("100200300", "salam");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static object Attempt(int number)
        => new { number, printer = "Fake", spoolerJobId = $"F-{number}", startedAt = DateTime.UtcNow, finishedAt = DateTime.UtcNow, counterBefore = 100L, counterAfter = 101L, counterUnit = "impressions", recovered = false };

    private static HttpRequestMessage NewOrder()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = 1,
                options = new { copies = 1, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = false,
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return request;
    }

    private static async Task BeatAsync(TestKiosk kiosk, bool printerOnline, string? problem = null, bool safeMode = false)
    {
        var response = await kiosk.Client.PostAsJsonAsync(
            "/api/kiosks/heartbeat", new { appVersion = "0.7.0", printerOnline, printerProblem = problem, safeMode });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The printer is down and stays down: a first bad heartbeat, then one
    /// after the wait (<c>Kiosk.PrinterFaultAfterSeconds</c>) has passed - the
    /// time is moved in the database rather than slept through.
    /// </summary>
    private async Task StopPrinterAsync(TestKiosk kiosk, string problem)
    {
        await BeatAsync(kiosk, printerOnline: false, problem: problem);

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
                Builders<Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId),
                Builders<Kiosk>.Update.Set(k => k.PrinterDownSince, DateTime.UtcNow.AddSeconds(-45)));
        }

        await BeatAsync(kiosk, printerOnline: false, problem: problem);
    }

    private async Task LastHeardAsync(Guid kioskId, DateTime at)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(k => k.Id, kioskId), Builders<Kiosk>.Update.Set(k => k.LastHeartbeatAt, at));
    }

    /// <summary>An order placed at the kiosk and paid from a registered user's wallet: Queued, and theirs.</summary>
    private async Task<(Guid JobId, decimal Price)> RegisteredJobAsync(TestKiosk kiosk, TestUser user)
    {
        using var topUp = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount = 5m }) };
        topUp.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(topUp)).EnsureSuccessStatusCode();

        var job = await PlaceAsync(kiosk.Client);
        var jobId = job.GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, jobId, "MobileApp");

        using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{intent.GetProperty("id").GetGuid()}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Wallet", useBonus = false }),
        };
        pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(pay)).EnsureSuccessStatusCode();

        var queued = await EventuallyAsync(() => JobAsync(jobId), stored => stored.Status == PrintJobStatus.Queued, "the paid order joining the queue");
        queued.UserId.Should().Be(user.Id, "an order paid from the app belongs to who paid");

        return (jobId, intent.GetProperty("amount").GetDecimal());
    }

    private static async Task<decimal> WalletAsync(TestUser user)
        => (await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet")).GetProperty("balance").GetDecimal();

    private static Task<HttpResponseMessage> ReportIncidentAsync(HttpClient client, string type)
        => client.PostAsJsonAsync("/api/incidents", new { type });

    private async Task<TResult> SendAsync<TResult>(IRequest<TResult> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<Kiosk> KioskAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.Find(k => k.Id == id).SingleAsync();
    }

    private async Task<PrintJob> JobAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.Find(job => job.Id == id).SingleAsync();
    }

    private async Task<List<Incident>> IncidentsAsync(Guid kioskId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
            .Find(incident => incident.KioskId == kioskId).SortBy(incident => incident.CreatedAt).ToListAsync();
    }

    private async Task<List<Refund>> RefundsAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Refunds.Find(refund => refund.PaymentId == paymentId).ToListAsync();
    }

    private async Task<List<Notification>> NotificationsAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Notifications.Find(n => n.UserId == userId).ToListAsync();
    }

    private const string AdminPassword = "OutageAdminPassword!2026";

    /// <summary>An account with one new role of exactly these permissions.</summary>
    private async Task<AdminUser> AdminAccountAsync(KioskScope kioskScope, string? telegramChatId, params string[] permissions)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Outage role {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());
        var roleId = (await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var scope = _fixture.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

        var account = new AdminUser
        {
            Email = $"outage.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Outage operator",
            RoleIds = [roleId],
            KioskScope = kioskScope,
            IsActive = true,
            TelegramChatId = telegramChatId,
        };
        account.PasswordHash = hasher.HashPassword(account, AdminPassword);

        await scope.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        return account;
    }

    private async Task<HttpClient> ScopedAdminAsync(KioskScope kioskScope, params string[] permissions)
    {
        var account = await AdminAccountAsync(kioskScope, null, permissions);
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = AdminPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }
}
