using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Schedules;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Pricing;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Schedules;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Scheduled printing (plan section 9.5, step 5 / M4; <c>docs/DECISIONS.md</c>
/// T-145): a kiosk, a file and a time paid in the app; "Çapa ver" (also before
/// the time), "Vaxtı dəyiş", cancelling, and the background jobs that ask the
/// user when the time comes and refund the ones nobody answers.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ScheduleTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public ScheduleTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task A_scheduled_print_is_paid_moved_and_sent_from_the_app_before_its_time()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "schedule-send");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        // Too soon, too far, or at a price the kiosk no longer asks: nothing is made.
        var soon = await CreateAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddMinutes(5));
        soon.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await soon.Content.ReadAsStringAsync()).Should().Contain("15");
        var far = await CreateAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddDays(31));
        far.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await far.Content.ReadAsStringAsync()).Should().Contain("30");
        var stale = await CreateAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(2), expectedAmount: 0.20m);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("printJobs.priceChanged");
        (await ListAsync(user.Client)).Should().BeEmpty();

        var (scheduleId, jobId, _) = await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(2));

        // Paid and waiting for its time: not in the queue, no paper set aside, not on the kiosk's QR list.
        var listed = await EventuallyAsync(
            async () => (await ListAsync(user.Client)).Single(s => s.GetProperty("id").GetGuid() == scheduleId),
            s => s.GetProperty("status").GetString() == "Scheduled",
            "the schedule paid");
        listed.GetProperty("jobStatus").GetString().Should().Be("Paid");
        listed.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        listed.GetProperty("amount").GetDecimal().Should().Be(0.30m);

        // On the home tab it is "Planlanıb · {time}", not a paid order waiting at the kiosk (T-158).
        var mine = (await user.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my")).EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == jobId);
        mine.GetProperty("status").GetString().Should().Be("Paid");
        mine.GetProperty("scheduledAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(listed.GetProperty("scheduledAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).ToString().Should().NotContain(jobId.ToString());
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IPrintJobRepository>().ReservedSheetsAsync(kiosk.KioskId, "A4")).Should().Be(0);
        }

        var code = (await user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/qr")).GetProperty("code").GetString();
        var scanned = await (await kiosk.Client.PostAsJsonAsync("/api/print-jobs/pending-for-user", new { code })).Content.ReadFromJsonAsync<JsonElement>();
        scanned.GetProperty("items").GetArrayLength().Should().Be(0, "a scheduled print is printed from the app only");

        // Its file stays while it waits.
        var delete = await user.Client.DeleteAsync($"/api/files/{fileId}");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await delete.Content.ReadAsStringAsync()).Should().Contain("files.inUseBySchedule");

        // Another time: the same rules.
        (await RescheduleAsync(user.Client, scheduleId, DateTime.UtcNow.AddMinutes(3))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var later = DateTime.UtcNow.AddHours(5);
        var moved = await RescheduleAsync(user.Client, scheduleId, later);
        moved.StatusCode.Should().Be(HttpStatusCode.OK, await moved.Content.ReadAsStringAsync());
        (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduledAt").GetDateTime().ToUniversalTime()
            .Should().BeCloseTo(later, TimeSpan.FromSeconds(1));

        // The kiosk is not printing: the reason, and nothing sent.
        await SetKioskStatusAsync(kiosk.KioskId, KioskStatus.Paused);
        var paused = await PrintNowAsync(user.Client, scheduleId);
        paused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await paused.Content.ReadAsStringAsync()).Should().Contain(kiosk.KioskCode).And.NotContain("Paused", "the status is said in words");
        await SetKioskStatusAsync(kiosk.KioskId, KioskStatus.Online);

        // "Çapa ver" hours early: into the queue; pressed twice, still once.
        var sent = await PrintNowAsync(user.Client, scheduleId);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var view = await sent.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("status").GetString().Should().Be("Sent");
        view.GetProperty("jobStatus").GetString().Should().Be("Queued");
        (await PrintNowAsync(user.Client, scheduleId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my")).EnumerateArray().Single(j => j.GetProperty("id").GetGuid() == jobId)
            .TryGetProperty("scheduledAt", out var sentAt).Should().BeFalse("sent: an ordinary order now, not {0}", sentAt);

        // Printed: its owner is told, once, opening the order (T-146).
        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Completed", sides: 3, sheets: 3);
        var printed = await EventuallyAsync(
            () => NotificationTests.ListAsync(user.Client, "az"),
            p => p.GetProperty("items").EnumerateArray().Any(n => n.GetProperty("type").GetString() == "JobCompleted"),
            "the job completed notification");
        var note = printed.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "JobCompleted");
        note.GetProperty("title").GetString().Should().Be("Çap hazırdır");
        note.GetProperty("body").GetString().Should().Contain(kiosk.KioskCode);
        note.GetProperty("link").GetString().Should().Be($"neoprint://jobs/{jobId}");

        // Sent: an ordinary order now, neither moved nor cancelled from this list.
        (await user.Client.DeleteAsync($"/api/schedules/{scheduleId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RescheduleAsync(user.Client, scheduleId, DateTime.UtcNow.AddHours(6))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Someone else's: not found.
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        (await PrintNowAsync(stranger.Client, scheduleId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PdfiumFact]
    public async Task When_the_time_comes_the_user_is_asked_and_an_unanswered_or_cancelled_one_is_refunded_to_the_wallet()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "schedule-due");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        // A phone in Azerbaijani and a browser in English (T-146).
        var phone = $"phone-{Guid.NewGuid():N}";
        var browser = $"browser-{Guid.NewGuid():N}";
        await NotificationTests.RegisterDeviceAsync(user.Client, phone, "az", "android");
        await NotificationTests.RegisterDeviceAsync(user.Client, browser, "en", "web");

        var (dueId, dueJob, duePayment) = await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(1));
        var (cancelledId, cancelledJob, cancelledPayment) =
            await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(1));

        foreach (var id in new[] { dueId, cancelledId })
        {
            await EventuallyAsync(() => FindAsync(id), s => s.Status == ScheduledPrintStatus.Scheduled, "the schedule paid");
        }

        // The owner cancels one: the money goes back.
        (await user.Client.DeleteAsync($"/api/schedules/{cancelledId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.DeleteAsync($"/api/schedules/{cancelledId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PrintNowAsync(user.Client, cancelledId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The other one's time came a minute ago.
        await UpdateAsync(dueId, Builders<ScheduledPrint>.Update.Set(s => s.ScheduledAt, DateTime.UtcNow.AddMinutes(-1)));
        (await SendAsync(new RunDueSchedulesCommand())).Should().BeGreaterThanOrEqualTo(1);

        var due = await FindAsync(dueId);
        due.Status.Should().Be(ScheduledPrintStatus.AwaitingUser);
        due.RemindedAt.Should().NotBeNull();
        due.AwaitingSince.Should().NotBeNull();
        var listed = (await ListAsync(user.Client)).Single(s => s.GetProperty("id").GetGuid() == dueId);
        listed.GetProperty("status").GetString().Should().Be("AwaitingUser");
        listed.GetProperty("autoCancelAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(2));

        // The push: one per device, each in its own language, opening the schedule.
        var pushed = await EventuallyAsync(() => Task.FromResult(_fixture.Pushes.To(phone)), p => p.Count == 1, "the push to the phone");
        pushed[0].Title.Should().Be("Planlanmış çapın vaxtı çatdı");
        pushed[0].Body.Should().Contain(kiosk.KioskCode);
        pushed[0].Data["type"].Should().Be("ScheduleDue");
        pushed[0].Data["link"].Should().Be($"neoprint://schedules/{dueId}");
        (await EventuallyAsync(() => Task.FromResult(_fixture.Pushes.To(browser)), p => p.Count == 1, "the push to the browser"))[0]
            .Title.Should().Be("Time for your scheduled print");

        // A second run asks nobody twice.
        await SendAsync(new RunDueSchedulesCommand());
        (await FindAsync(dueId)).RemindedAt.Should().Be(due.RemindedAt);
        await Task.Delay(1500);
        _fixture.Pushes.To(phone).Should().ContainSingle();

        // The list reads in the app's language, and opening it reads it.
        var page = await NotificationTests.ListAsync(user.Client, "ru");
        var dueNote = page.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "ScheduleDue");
        dueNote.GetProperty("title").GetString().Should().Be("Время печати по расписанию");
        page.GetProperty("unread").GetInt64().Should().Be(1);
        (await user.Client.PostAsync($"/api/notifications/{dueNote.GetProperty("id").GetGuid()}/read", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await NotificationTests.ListAsync(user.Client, "az")).GetProperty("unread").GetInt64().Should().Be(0);

        // Nobody answered for a day: cancelled and refunded.
        await UpdateAsync(dueId, Builders<ScheduledPrint>.Update.Set(s => s.AwaitingSince, DateTime.UtcNow.AddHours(-25)));
        (await SendAsync(new AutoCancelSchedulesCommand())).Should().BeGreaterThanOrEqualTo(1);
        (await FindAsync(dueId)).Status.Should().Be(ScheduledPrintStatus.Expired);
        (await FindAsync(cancelledId)).Status.Should().Be(ScheduledPrintStatus.Cancelled);

        foreach (var (jobId, paymentId) in new[] { (dueJob, duePayment), (cancelledJob, cancelledPayment) })
        {
            (await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}")).GetProperty("status").GetString().Should().Be("Cancelled");

            var refund = await EventuallyAsync(
                async () =>
                {
                    using var scope = _fixture.Services.CreateScope();
                    return await scope.ServiceProvider.GetRequiredService<MongoContext>().Refunds
                        .Find(r => r.PaymentId == paymentId).FirstOrDefaultAsync();
                },
                r => r is { Status: RefundStatus.Succeeded },
                "the refund");

            refund!.Reason.Should().Be(RefundReason.ScheduleCancel);
            refund.Destination.Should().Be(RefundDestination.Wallet, "a scheduled print's money goes back to the balance");
        }

        // Told once that it was cancelled for want of an answer, with the money back;
        // no separate refund notice for either - the user cancelled one in the app.
        var cancelledPush = await EventuallyAsync(
            () => Task.FromResult(_fixture.Pushes.To(phone).Where(p => p.Data["type"] == "ScheduleAutoCancelled").ToList()),
            p => p.Count == 1,
            "the auto-cancel push");
        cancelledPush[0].Body.Should().Contain("0,30 ₼");
        await SendAsync(new AutoCancelSchedulesCommand());
        var types = (await NotificationTests.ListAsync(user.Client, "az")).GetProperty("items").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()).ToList();
        types.Should().BeEquivalentTo(["ScheduleDue", "ScheduleAutoCancelled"]);

        // Both closed, the file is free again.
        (await user.Client.DeleteAsync($"/api/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [PdfiumFact]
    public async Task A_schedule_never_paid_is_cancelled_with_its_order_and_left_off_the_list()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "schedule-unpaid");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        var created = await CreateAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(1));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var scheduleId = body.GetProperty("scheduleId").GetGuid();
        var jobId = body.GetProperty("jobId").GetGuid();

        var pending = (await ListAsync(user.Client)).Single(s => s.GetProperty("id").GetGuid() == scheduleId);
        pending.GetProperty("status").GetString().Should().Be("PendingPayment");
        pending.GetProperty("paymentId").GetGuid().Should().Be(body.GetProperty("paymentId").GetGuid(), "the app pays it from the list");
        (await PrintNowAsync(user.Client, scheduleId)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await user.Client.DeleteAsync($"/api/schedules/{scheduleId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindAsync(scheduleId)).Status.Should().Be(ScheduledPrintStatus.Cancelled);
        (await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}")).GetProperty("status").GetString().Should().Be("Cancelled");
        (await ListAsync(user.Client)).Should().NotContain(s => s.GetProperty("id").GetGuid() == scheduleId);
    }

    private async Task<(Guid ScheduleId, Guid JobId, Guid PaymentId)> ScheduleAndPayAsync(
        HttpClient user, string kioskCode, Guid fileId, DateTime at)
    {
        var created = await CreateAsync(user, kioskCode, fileId, at);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = body.GetProperty("paymentId").GetGuid();
        body.GetProperty("amount").GetDecimal().Should().Be(0.30m);

        var paid = await PostAsync(user, $"/api/payments/{paymentId}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        // The payment screen says when, not "scan your QR at the kiosk".
        var view = await user.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/app");
        view.GetProperty("collectAtKiosk").GetBoolean().Should().BeFalse();
        view.GetProperty("scheduledAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(at, TimeSpan.FromSeconds(1));

        return (body.GetProperty("scheduleId").GetGuid(), body.GetProperty("jobId").GetGuid(), paymentId);
    }

    /// <summary><c>docs/DECISIONS.md</c> T-155: paid when scheduled, never priced again.</summary>
    [PdfiumFact]
    public async Task A_scheduled_print_keeps_the_price_it_was_paid_at_when_the_kiosk_prices_change()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "schedule-price");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);
        var (scheduleId, jobId, paymentId) = await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(2));
        await EventuallyAsync(
            async () => (await ListAsync(user.Client)).Single(s => s.GetProperty("id").GetGuid() == scheduleId),
            s => s.GetProperty("status").GetString() == "Scheduled",
            "the schedule paid");

        // The kiosk's prices go up: a new order is priced by them...
        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            var dearer = new PricingProfile { Name = $"Dearer {Guid.NewGuid():N}", BwPricePerSide = Money.FromAzn(0.50m) };
            await context.PricingProfiles.InsertOneAsync(dearer);
            await context.Kiosks.UpdateOneAsync(
                Builders<Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId), Builders<Kiosk>.Update.Set(k => k.PricingProfileId, dearer.Id));
        }

        (await CreateAsync(user.Client, kiosk.KioskCode, fileId, DateTime.UtcNow.AddHours(3))).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "the new prices apply to a new order");

        // ...the one already paid is moved and sent at the price it was paid at, with no second payment.
        (await RescheduleAsync(user.Client, scheduleId, DateTime.UtcNow.AddHours(4))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListAsync(user.Client)).Single(s => s.GetProperty("id").GetGuid() == scheduleId)
            .GetProperty("amount").GetDecimal().Should().Be(0.30m);
        var sent = await PrintNowAsync(user.Client, scheduleId);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("amount").GetDecimal().Should().Be(0.30m);

        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            var job = await context.PrintJobs.Find(j => j.Id == jobId).SingleAsync();
            job.Price.Total.Should().Be(Money.FromAzn(0.30m));
            job.PaymentId.Should().Be(paymentId);
            (await context.Payments.CountDocumentsAsync(p => p.JobId == jobId)).Should().Be(1);
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient user, string kioskCode, Guid fileId, DateTime at, decimal expectedAmount = 0.30m)
        => PostAsync(user, "/api/schedules", new { kiosk = kioskCode, fileId, options = BlackAndWhite, scheduledAt = at, expectedAmount });

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(HttpClient user)
        => [.. (await user.GetFromJsonAsync<JsonElement>("/api/schedules")).GetProperty("items").EnumerateArray()];

    private static Task<HttpResponseMessage> RescheduleAsync(HttpClient user, Guid scheduleId, DateTime at)
        => user.PutAsJsonAsync($"/api/schedules/{scheduleId}/time", new { scheduledAt = at });

    private static Task<HttpResponseMessage> PrintNowAsync(HttpClient user, Guid scheduleId)
        => PostAsync(user, $"/api/schedules/{scheduleId}/print-now", new { });

    private async Task<ScheduledPrint> FindAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().ScheduledPrints.Find(s => s.Id == id).SingleAsync();
    }

    private async Task UpdateAsync(Guid id, UpdateDefinition<ScheduledPrint> update)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().ScheduledPrints
            .UpdateOneAsync(Builders<ScheduledPrint>.Filter.Eq(s => s.Id, id), update);
    }

    private async Task SetKioskStatusAsync(Guid kioskId, KioskStatus status)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(k => k.Id, kioskId), Builders<Kiosk>.Update.Set(k => k.Status, status));
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }
}
