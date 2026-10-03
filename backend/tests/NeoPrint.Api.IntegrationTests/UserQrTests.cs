using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.UserQr;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Printing with the personal QR (plan section 9.4, step 5 / M3;
/// <c>docs/DECISIONS.md</c> T-144): the rotating QR, the kiosk's grant, an
/// unpaid preparation priced and paid at the kiosk, and one paid in advance
/// for one kiosk, collected there or cancelled to the wallet.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UserQrTests
{
    private const string Grant = "X-User-Qr-Grant";

    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public UserQrTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_qr_is_signed_rotates_and_opens_the_user_s_files_at_one_kiosk_only()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "qr-scan");
        using var other = await ReadyKioskAsync(_fixture, "qr-other");
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        var qr = await user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/qr");
        var code = qr.GetProperty("code").GetString()!;
        code.Should().StartWith("NPU:").And.NotContain(user.Email);
        qr.GetProperty("rotationSeconds").GetInt32().Should().Be(60);

        var png = await user.Client.GetAsync("/api/users/me/qr.png");
        png.StatusCode.Should().Be(HttpStatusCode.OK);
        png.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        png.Headers.GetValues("X-Qr-Expires-At").Should().ContainSingle();
        png.Headers.CacheControl!.NoStore.Should().BeTrue();

        // Tampered, foreign or old: refused, without saying whose it was.
        var tampered = await ScanAsync(kiosk.Client, code[..^2] + (code[^1] == 'A' ? "BB" : "AA"));
        tampered.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tampered.Content.ReadAsStringAsync()).Should().Contain("userQr.invalid");
        (await (await ScanAsync(kiosk.Client, "MOCKBANK:123")).Content.ReadAsStringAsync()).Should().Contain("userQr.invalid");

        var old = _fixture.Services.GetRequiredService<UserQrCodes>().Issue(user.Id, DateTime.UtcNow.AddMinutes(-2));
        (await (await ScanAsync(kiosk.Client, old)).Content.ReadAsStringAsync()).Should().Contain("userQr.expired");

        // A user's QR is not a user's token.
        (await user.Client.PostAsJsonAsync("/api/print-jobs/pending-for-user", new { code })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var scanned = await ScanAsync(kiosk.Client, code);
        scanned.StatusCode.Should().Be(HttpStatusCode.OK, await scanned.Content.ReadAsStringAsync());
        var files = await scanned.Content.ReadFromJsonAsync<JsonElement>();
        files.GetProperty("items").GetArrayLength().Should().Be(0);
        var grant = files.GetProperty("grant").GetGuid();

        (await ListAsync(kiosk.Client, grant)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The grant is this kiosk's only.
        var stolen = await ListAsync(other.Client, grant);
        stolen.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stolen.Content.ReadAsStringAsync()).Should().Contain("userQr.grantExpired");
    }

    [PdfiumFact]
    public async Task An_unpaid_preparation_is_priced_and_paid_at_the_kiosk_that_reads_the_qr()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "qr-prepared");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 2);

        // Only the colour is chosen; the rest is the kiosk's to ask.
        var created = await PostAsync(user.Client, "/api/prepared-prints", new { fileId, options = new { color = false } });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var preparedId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var badRange = await PostAsync(user.Client, "/api/prepared-prints", new { fileId, options = new { pageRange = "1-9" } });
        badRange.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var mine = await user.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints");
        var listed = mine.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == preparedId);
        listed.GetProperty("kind").GetString().Should().Be("Prepared");
        listed.GetProperty("state").GetString().Should().Be("Ready");
        listed.GetProperty("preset").GetProperty("color").GetBoolean().Should().BeFalse();

        var files = await (await ScanAsync(kiosk.Client, await CodeAsync(user.Client))).Content.ReadFromJsonAsync<JsonElement>();
        var grant = files.GetProperty("grant").GetGuid();
        var item = files.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("kind").GetString().Should().Be("Prepared");
        item.GetProperty("pages").GetInt32().Should().Be(2);
        item.GetProperty("here").GetBoolean().Should().BeTrue();

        // Ordered at the kiosk with its own options; a retry is the same order.
        var key = Guid.NewGuid().ToString("N");
        var order = await OrderAsync(kiosk.Client, grant, preparedId, key);
        order.StatusCode.Should().Be(HttpStatusCode.OK, await order.Content.ReadAsStringAsync());
        var job = await order.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = job.GetProperty("id").GetGuid();
        job.GetProperty("source").GetString().Should().Be("UserQr");
        job.GetProperty("status").GetString().Should().Be("PendingPayment");
        (await (await OrderAsync(kiosk.Client, grant, preparedId, key)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid().Should().Be(jobId);

        // Paid on the kiosk's own payment screen, then printed from the server's PDF.
        var intent = await IntentAsync(kiosk.Client, jobId);
        (await SimulateAsync(_fixture, intent.GetProperty("id").GetGuid())).StatusCode.Should().Be(HttpStatusCode.OK);
        await EventuallyAsync(
            () => kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}"),
            j => j.GetProperty("status").GetString() == "Queued",
            "the order in the queue");
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.OK);

        // In the queue: no longer offered, and the app says it is printing.
        (await (await ListAsync(kiosk.Client, grant)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetArrayLength().Should().Be(0);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == preparedId).GetProperty("state").GetString().Should().Be("Printing");

        (await user.Client.DeleteAsync($"/api/prepared-prints/{preparedId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints")).GetProperty("items").EnumerateArray()
            .Should().NotContain(i => i.GetProperty("id").GetGuid() == preparedId);
    }

    [PdfiumFact]
    public async Task A_preparation_paid_in_advance_waits_for_its_kiosk_and_its_owner_s_qr()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "qr-paid");
        using var other = await ReadyKioskAsync(_fixture, "qr-paid-other");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        // The price the app showed is not the kiosk's: asked again, nothing made.
        var stale = await PostAsync(user.Client, "/api/prepared-prints/paid", new { kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, expectedAmount = 0.20m });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("printJobs.priceChanged").And.Contain("0,30");

        var (jobId, _) = await PaidPreparationAsync(user.Client, kiosk.KioskCode, fileId);

        // Paid, and waiting: not in the queue, no file for the kiosk yet, no paper reserved.
        var held = await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}");
        held.GetProperty("status").GetString().Should().Be("Paid");
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).ToString().Should().NotContain(jobId.ToString());
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IPrintJobRepository>().ReservedSheetsAsync(kiosk.KioskId, "A4")).Should().Be(0);
        }

        var mine = (await user.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == jobId);
        mine.GetProperty("kind").GetString().Should().Be("Paid");
        mine.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        mine.GetProperty("collectBy").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddDays(6));

        // Another kiosk shows it, but it is not that kiosk's to print.
        var elsewhere = await (await ScanAsync(other.Client, await CodeAsync(user.Client))).Content.ReadFromJsonAsync<JsonElement>();
        var shown = elsewhere.GetProperty("items").EnumerateArray().Single();
        shown.GetProperty("here").GetBoolean().Should().BeFalse();
        shown.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        var refused = await ReleaseAsync(other.Client, elsewhere.GetProperty("grant").GetGuid(), jobId);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain(kiosk.KioskCode);

        // Its own kiosk, its owner's QR: into the queue; picked twice, still once.
        var here = await (await ScanAsync(kiosk.Client, await CodeAsync(user.Client))).Content.ReadFromJsonAsync<JsonElement>();
        var grant = here.GetProperty("grant").GetGuid();
        here.GetProperty("items").EnumerateArray().Single().GetProperty("here").GetBoolean().Should().BeTrue();

        var released = await ReleaseAsync(kiosk.Client, grant, jobId);
        released.StatusCode.Should().Be(HttpStatusCode.OK, await released.Content.ReadAsStringAsync());
        (await released.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Queued");
        (await ReleaseAsync(kiosk.Client, grant, jobId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Collected: the owner cannot cancel it any more from the list.
        (await user.Client.PostAsync($"/api/prepared-prints/paid/{jobId}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [PdfiumFact]
    public async Task A_paid_preparation_not_collected_in_time_is_cancelled_and_refunded_to_the_wallet()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "qr-uncollected");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        var (late, latePayment) = await PaidPreparationAsync(user.Client, kiosk.KioskCode, fileId);
        var (cancelled, cancelledPayment) = await PaidPreparationAsync(user.Client, kiosk.KioskCode, fileId);

        // The owner cancels one.
        (await user.Client.PostAsync($"/api/prepared-prints/paid/{cancelled}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The other was paid eight days ago and nobody came.
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.UpdateOneAsync(
                Builders<PrintJob>.Filter.Eq(j => j.Id, late), Builders<PrintJob>.Update.Set(j => j.PaidAt, DateTime.UtcNow.AddDays(-8)));
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelUncollectedPrintJobsCommand())).Should().BeGreaterThanOrEqualTo(1);
        }

        foreach (var (jobId, paymentId, reason) in new[]
                 {
                     (late, latePayment, RefundReason.NotCollected),
                     (cancelled, cancelledPayment, RefundReason.UserCancel),
                 })
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

            refund!.Reason.Should().Be(reason);
            refund.Destination.Should().Be(RefundDestination.Wallet, "a registered user's money goes back to the wallet");
        }

        (await user.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints")).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    private static async Task<(Guid JobId, Guid PaymentId)> PaidPreparationAsync(HttpClient user, string kioskCode, Guid fileId)
    {
        var placed = await PostAsync(user, "/api/prepared-prints/paid", new { kiosk = kioskCode, fileId, options = BlackAndWhite, expectedAmount = 0.30m });
        placed.StatusCode.Should().Be(HttpStatusCode.OK, await placed.Content.ReadAsStringAsync());
        var order = await placed.Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = order.GetProperty("paymentId").GetGuid();

        var paid = await PostAsync(user, $"/api/payments/{paymentId}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var view = await user.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/app");
        view.GetProperty("collectAtKiosk").GetBoolean().Should().BeTrue();

        return (order.GetProperty("jobId").GetGuid(), paymentId);
    }

    private static async Task<string> CodeAsync(HttpClient user)
        => (await user.GetFromJsonAsync<JsonElement>("/api/users/me/qr")).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> ScanAsync(HttpClient kiosk, string code)
        => kiosk.PostAsJsonAsync("/api/print-jobs/pending-for-user", new { code });

    private static async Task<HttpResponseMessage> ListAsync(HttpClient kiosk, Guid grant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/print-jobs/pending-for-user");
        request.Headers.Add(Grant, grant.ToString());
        return await kiosk.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> OrderAsync(HttpClient kiosk, Guid grant, Guid preparedId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/pending-for-user/prepared/{preparedId}")
        {
            Content = JsonContent.Create(new { options = BlackAndWhite }),
        };

        request.Headers.Add(Grant, grant.ToString());
        request.Headers.Add("Idempotency-Key", key);
        return await kiosk.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReleaseAsync(HttpClient kiosk, Guid grant, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/pending-for-user/paid/{jobId}/release");
        request.Headers.Add(Grant, grant.ToString());
        return await kiosk.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }
}
