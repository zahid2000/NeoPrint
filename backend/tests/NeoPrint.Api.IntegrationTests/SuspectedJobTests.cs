using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Payments;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Scenario B (plan section 2.5): the kiosk goes quiet and nobody knows
/// whether the job printed. Suspected, never refunded automatically; the kiosk
/// coming back or an admin decides (docs/TASKS.md T-104).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SuspectedJobTests
{
    private static readonly object Attempt = new
    {
        number = 1,
        printer = "Fake",
        spoolerJobId = "F-9",
        startedAt = DateTime.UtcNow,
        counterBefore = 700L,
        counterUnit = "impressions",
        recovered = false,
    };

    private readonly NeoPrintApiFixture _fixture;

    public SuspectedJobTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_silent_kiosk_makes_its_job_suspected_with_one_incident_and_no_refund()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "suspect-watch");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);
        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: Attempt);

        // Not yet: the kiosk printed a minute ago.
        await WatchAsync();
        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Printing);

        await SilenceAsync(kiosk.KioskId, jobId);
        await WatchAsync();
        await WatchAsync();

        var job = await JobAsync(jobId);
        job.Status.Should().Be(PrintJobStatus.Suspected);
        job.SuspectedAt.Should().NotBeNull();

        (await IncidentsAsync(jobId, open: true)).Should().ContainSingle(i => i.Type == IncidentType.JobSuspected && i.Severity == IncidentSeverity.High);
        (await RefundsForAsync(paymentId)).Should().BeEmpty("the outcome is unknown: no automatic refund");
        (await PaymentAsync(paymentId)).Status.Should().Be(PaymentStatus.Succeeded);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/print-jobs/suspected");
        var item = list.EnumerateArray().Single(entry => entry.GetProperty("jobId").GetGuid() == jobId);
        item.GetProperty("overdue").GetBoolean().Should().BeFalse();
        item.GetProperty("paymentMethod").GetString().Should().Be("BankQr");

        var card = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs/{jobId}/investigation");
        card.GetProperty("payment").GetProperty("refundable").GetDecimal().Should().Be(price);
        card.GetProperty("detail").GetProperty("attempts")[0].GetProperty("counterBefore").GetInt64().Should().Be(700);
        card.GetProperty("kioskIncidents").EnumerateArray().Should().Contain(i => i.GetProperty("type").GetString() == "JobSuspected");
    }

    [DockerFact]
    public async Task The_kiosk_coming_back_settles_the_job_itself_printed_or_refunded()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "suspect-back");

        // It printed: Completed, nothing goes back.
        var (printedJob, printedPayment, _) = await SuspectedBankJobAsync(kiosk);
        await ReportAsync(kiosk.Client, printedJob, "Printing", attempt: Attempt);
        (await JobAsync(printedJob)).Status.Should().Be(PrintJobStatus.Suspected, "\"still printing\" is not an outcome");

        await ReportAsync(kiosk.Client, printedJob, "Completed", sides: 3, sheets: 3, attempt: Attempt);
        (await JobAsync(printedJob)).Status.Should().Be(PrintJobStatus.Completed);
        (await RefundsForAsync(printedPayment)).Should().BeEmpty();
        (await IncidentsAsync(printedJob, open: true)).Should().BeEmpty("the kiosk's report closed the investigation");

        // It did not print: scenario A, the card gets it all back.
        var (lostJob, lostPayment, price) = await SuspectedBankJobAsync(kiosk);
        await ReportAsync(kiosk.Client, lostJob, "Interrupted", errorCode: "printer.restarted", attempt: Attempt);

        var refund = await EventuallyAsync(() => RefundsForAsync(lostPayment), r => r.Any(x => x.Status == RefundStatus.Succeeded), "scenario A's refund");
        refund.Should().ContainSingle().Which.Amount.Amount.Should().Be(price);
        (await JobAsync(lostJob)).Status.Should().Be(PrintJobStatus.Cancelled);
    }

    [DockerFact]
    public async Task An_admin_decides_with_a_reason_and_a_note_once_and_a_late_kiosk_report_changes_nothing()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "suspect-admin");
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        // "Tam qaytar".
        var (fullJob, fullPayment, price) = await SuspectedBankJobAsync(kiosk);
        (await ResolveAsync(admin, fullJob, "FullRefund", note: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the note is mandatory");

        var full = await ResolveAsync(admin, fullJob, "FullRefund");
        full.StatusCode.Should().Be(HttpStatusCode.OK, await full.Content.ReadAsStringAsync());
        (await JobAsync(fullJob)).Status.Should().Be(PrintJobStatus.Cancelled);
        await EventuallyAsync(() => PaymentAsync(fullPayment), p => p.Status == PaymentStatus.Refunded, "the full refund");
        (await RefundsForAsync(fullPayment)).Should().ContainSingle().Which.Amount.Amount.Should().Be(price);

        var again = await ResolveAsync(admin, fullJob, "NoRefund");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("printJobs.suspected.alreadyResolved");

        // The kiosk comes back afterwards and says it printed: kept, shown, not undone.
        await ReportAsync(kiosk.Client, fullJob, "Completed", sides: 3, sheets: 3, attempt: Attempt);
        (await JobAsync(fullJob)).Status.Should().Be(PrintJobStatus.Cancelled);
        (await IncidentsAsync(fullJob, open: true)).Should().ContainSingle(i => i.DedupKey == $"suspected-late:{fullJob}");

        // "Hissəvi qaytar".
        var (partJob, partPayment, _) = await SuspectedBankJobAsync(kiosk);
        (await ResolveAsync(admin, partJob, "PartialRefund")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the amount is mandatory");
        (await ResolveAsync(admin, partJob, "PartialRefund", amount: 0.10m)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JobAsync(partJob)).Status.Should().Be(PrintJobStatus.Completed);
        await EventuallyAsync(() => PaymentAsync(partPayment), p => p.Status == PaymentStatus.PartiallyRefunded, "the partial refund");
        (await PaymentAsync(partPayment)).RefundedAmount.Amount.Should().Be(0.10m);

        // "Qaytarma yoxdur".
        var (keptJob, keptPayment, _) = await SuspectedBankJobAsync(kiosk);
        (await ResolveAsync(admin, keptJob, "NoRefund")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JobAsync(keptJob)).Status.Should().Be(PrintJobStatus.Completed);
        (await RefundsForAsync(keptPayment)).Should().BeEmpty();
        (await IncidentsAsync(keptJob, open: true)).Should().BeEmpty();

        // "Yenidən çapa göndər": back in the kiosk's queue, not paid again.
        var (reprintJob, reprintPayment, _) = await SuspectedBankJobAsync(kiosk);
        (await ResolveAsync(admin, reprintJob, "Reprint")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await JobAsync(reprintJob)).Status.Should().Be(PrintJobStatus.Queued);
        var queue = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue");
        queue.EnumerateArray().Should().Contain(entry => entry.GetProperty("jobId").GetGuid() == reprintJob);
        (await PaymentAsync(reprintPayment)).Status.Should().Be(PaymentStatus.Succeeded);

        // A plain "Ləğv et" also ends the investigation (default refund, no open incident left).
        var (cancelJob, cancelPayment, _) = await SuspectedBankJobAsync(kiosk);
        using (var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/print-jobs/{cancelJob}/cancel") { Content = JsonContent.Create(new { reason = "Kiosk qayıtmadı" }) })
        {
            cancel.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await admin.SendAsync(cancel)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await IncidentsAsync(cancelJob, open: true)).Should().BeEmpty();
        await EventuallyAsync(() => PaymentAsync(cancelPayment), p => p.Status == PaymentStatus.Refunded, "the cancellation's refund");

        using var scope = _fixture.Services.CreateScope();
        var audits = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(entry => entry.Action == "printJobs.suspected.resolve").ToListAsync();
        audits.Select(entry => entry.EntityId).Should().Contain([fullJob.ToString(), partJob.ToString(), keptJob.ToString(), reprintJob.ToString()]);
    }

    [DockerFact]
    public async Task A_job_nobody_decided_on_reminds_the_admins_once_and_nothing_else_happens()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "suspect-remind");
        var (jobId, paymentId, _) = await SuspectedBankJobAsync(kiosk);

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.UpdateOneAsync(
                Builders<PrintJob>.Filter.Eq(j => j.Id, jobId),
                Builders<PrintJob>.Update.Set(j => j.SuspectedAt, DateTime.UtcNow.AddHours(-73)));
        }

        await WatchAsync();
        await WatchAsync();

        var job = await JobAsync(jobId);
        job.Status.Should().Be(PrintJobStatus.Suspected, "time alone decides nothing");
        job.SuspectedReminderAt.Should().NotBeNull();
        (await IncidentsAsync(jobId, open: true)).Should().ContainSingle(i => i.DedupKey == $"suspected-reminder:{jobId}");
        (await RefundsForAsync(paymentId)).Should().BeEmpty();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/admin/print-jobs/suspected");
        list.EnumerateArray().Single(entry => entry.GetProperty("jobId").GetGuid() == jobId).GetProperty("overdue").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Customers_are_heard_by_job_code_or_in_the_app_and_the_claim_lands_on_the_investigation_card()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "suspect-claim");
        var (jobId, _, _) = await SuspectedBankJobAsync(kiosk);
        var job = await JobAsync(jobId);
        string kioskCode;

        using (var scope = _fixture.Services.CreateScope())
        {
            kioskCode = (await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.Find(k => k.Id == kiosk.KioskId).FirstAsync()).Code;
        }

        using var anonymous = _fixture.CreateApiClient();
        (await ClaimAsync(anonymous, "/api/support/claims", new { jobCode = job.JobCode, kioskCode = "K-0000", text = "Çap çıxmadı" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "both codes must match");
        (await ClaimAsync(anonymous, "/api/support/claims", new { jobCode = job.JobCode, kioskCode, text = " " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var opened = await ClaimAsync(anonymous, "/api/support/claims", new { jobCode = job.JobCode, kioskCode, text = "Pul çıxdı, kağız çıxmadı", contact = "+994 50 000 00 00" });
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var receipt = await opened.Content.ReadFromJsonAsync<JsonElement>();
        receipt.TryGetProperty("contact", out _).Should().BeFalse("the anonymous answer says nothing back");

        // In the app: only about the user's own job.
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        (await ClaimAsync(user.Client, "/api/me/claims", new { jobId, text = "Mənim deyil" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.UpdateOneAsync(
                Builders<PrintJob>.Filter.Eq(j => j.Id, jobId), Builders<PrintJob>.Update.Set(j => j.UserId, user.Id));
        }

        var mine = await ClaimAsync(user.Client, "/api/me/claims", new { jobId, text = "Problem barədə bildirirəm" });
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        var claimId = (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var card = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs/{jobId}/investigation");
        card.GetProperty("claims").GetArrayLength().Should().Be(2);
        card.GetProperty("claims").EnumerateArray()
            .Select(c => c.TryGetProperty("contact", out var contact) ? contact.GetString() : null)
            .Should().Contain("+994 50 000 00 00", "the admin sees how to reach the customer");

        (await admin.GetFromJsonAsync<JsonElement>("/api/admin/print-jobs/suspected"))
            .EnumerateArray().Single(entry => entry.GetProperty("jobId").GetGuid() == jobId).GetProperty("openClaims").GetInt32().Should().Be(2);

        var answer = await ClaimAsync(admin, $"/api/admin/support/claims/{claimId}/answer", new { reply = "Araşdırırıq, 3 iş günü ərzində cavab veriləcək." });
        answer.StatusCode.Should().Be(HttpStatusCode.OK);

        var own = await user.Client.GetFromJsonAsync<JsonElement>("/api/me/claims");
        var answered = own.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == claimId);
        answered.GetProperty("status").GetString().Should().Be("Answered");
        answered.GetProperty("reply").GetString().Should().StartWith("Araşdırırıq");

        (await ClaimAsync(admin, $"/api/admin/support/claims/{claimId}/close", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClaimAsync(admin, $"/api/admin/support/claims/{claimId}/answer", new { reply = "Yenə" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>A bank-paid job that started printing, then the kiosk went quiet and the watchdog noticed.</summary>
    private async Task<(Guid JobId, Guid PaymentId, decimal Price)> SuspectedBankJobAsync(TestKiosk kiosk)
    {
        var paid = await PaidBankJobAsync(_fixture, kiosk);
        await ReportAsync(kiosk.Client, paid.JobId, "Printing", attempt: Attempt);
        await SilenceAsync(kiosk.KioskId, paid.JobId);
        await WatchAsync();

        (await JobAsync(paid.JobId)).Status.Should().Be(PrintJobStatus.Suspected);

        return paid;
    }

    /// <summary>As if the kiosk's last heartbeat and the job's start were an hour ago.</summary>
    private async Task SilenceAsync(Guid kioskId, Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var hourAgo = DateTime.UtcNow.AddHours(-1);

        await context.Kiosks.UpdateOneAsync(Builders<Kiosk>.Filter.Eq(k => k.Id, kioskId), Builders<Kiosk>.Update.Set(k => k.LastHeartbeatAt, hourAgo));
        await context.PrintJobs.UpdateOneAsync(
            Builders<PrintJob>.Filter.Eq(j => j.Id, jobId),
            Builders<PrintJob>.Update.Set(j => j.StartedAt, hourAgo).Set(j => j.QueuedAt, hourAgo));
    }

    private async Task WatchAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new WatchStuckJobsCommand());
    }

    private async Task<List<Incident>> IncidentsAsync(Guid jobId, bool open)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
            .Find(i => i.EntityId == jobId && i.Open == open).ToListAsync();
    }

    private async Task<List<Refund>> RefundsForAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRefundRepository>().ListForPaymentAsync(paymentId)).ToList();
    }

    private async Task<Payment> PaymentAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().FindByIdAsync(paymentId))!;
    }

    private async Task<PrintJob> JobAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPrintJobRepository>().FindByIdAsync(jobId))!;
    }

    private static async Task<HttpResponseMessage> ResolveAsync(HttpClient admin, Guid jobId, string decision, decimal? amount = null, string? note = "Sayğac və kamera yoxlanıldı")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/print-jobs/{jobId}/resolve")
        {
            Content = JsonContent.Create(new { decision, amount, reason = "Kiosk qayıtmadı", note }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ClaimAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }
}
