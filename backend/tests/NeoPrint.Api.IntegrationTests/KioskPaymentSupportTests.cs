using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Files;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// What the kiosk's payment screens need from the backend (docs/TASKS.md
/// T-108): the refund info and its status page with a claim form, and the
/// flash file of an app-paid order joining the account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskPaymentSupportTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskPaymentSupportTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_refunded_order_shows_its_refund_and_its_status_page_takes_a_claim()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-page");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);

        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Interrupted", errorCode: "printer.paperJam");

        var refunds = await EventuallyAsync(
            async () => await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}/refunds"),
            list => list.GetArrayLength() == 1 && list[0].GetProperty("status").GetString() == "Succeeded",
            "the refund on the kiosk");

        var refund = refunds[0];
        var refundId = refund.GetProperty("id").GetGuid();
        refund.GetProperty("amount").GetDecimal().Should().Be(price);
        refund.GetProperty("destination").GetString().Should().Be("Card");
        refund.GetProperty("statusUrl").GetString().Should().EndWith($"/refund/{refundId}");

        // Another kiosk sees nothing of it.
        using var other = await ReadyKioskAsync(_fixture, "refund-other");
        (await other.Client.GetAsync($"/api/print-jobs/{jobId}/refunds")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The page the QR opens: plain HTML, in the asked language, no personal data, not cached.
        using var browser = _fixture.CreateApiClient();
        var page = await browser.GetAsync($"/refund/{refundId}?lang=en");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        page.Headers.CacheControl!.NoStore.Should().BeTrue();
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("The money was returned").And.Contain("To your bank card").And.Contain("0.30 ₼");
        html.Should().NotContain(paymentId.ToString(), "nothing but the refund itself");

        var az = await browser.GetStringAsync($"/refund/{refundId}");
        az.Should().Contain("Pul qaytarıldı");

        (await browser.GetAsync($"/refund/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // "Problem barədə bildir" from the page lands on the job.
        using (var claim = new HttpRequestMessage(HttpMethod.Post, $"/api/refunds/{refundId}/claim")
        {
            Content = JsonContent.Create(new { text = "Kağız sıxıldı, amma pul gəldi", contact = "test@example.com" }),
        })
        {
            claim.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await browser.SendAsync(claim)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<MongoContext>().SupportClaims.Find(c => c.JobId == jobId).CountDocumentsAsync()).Should().Be(1);
    }

    [DockerFact]
    public async Task The_flash_file_of_an_app_paid_order_joins_the_account_once()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        using (var adjust = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/wallets/{user.Id}/adjust")
        {
            Content = JsonContent.Create(new { amount = 5m, bonusPages = 0, reason = "Test: balans" }),
        })
        {
            adjust.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await admin.SendAsync(adjust)).EnsureSuccessStatusCode();
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "flash-upload");

        // Paid at the kiosk's bank QR: no account, nowhere to keep it.
        var (anonymousJob, _, _) = await PaidBankJobAsync(_fixture, kiosk);
        (await UploadAsync(kiosk.Client, anonymousJob)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var jobId = (await PlaceAsync(kiosk.Client)).GetProperty("id").GetGuid();
        var paymentId = (await IntentAsync(kiosk.Client, jobId, "MobileApp")).GetProperty("id").GetGuid();

        using (var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Wallet", useBonus = false }),
        })
        {
            pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await user.Client.SendAsync(pay)).EnsureSuccessStatusCode();
        }

        var first = await UploadAsync(kiosk.Client, jobId);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var kept = await first.Content.ReadFromJsonAsync<JsonElement>();
        var fileId = kept.GetProperty("fileId").GetGuid();

        var again = await (await UploadAsync(kiosk.Client, jobId)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("fileId").GetGuid().Should().Be(fileId);
        again.GetProperty("alreadyKept").GetBoolean().Should().BeTrue();

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var file = await context.Files.Find(f => f.Id == fileId).FirstAsync();
        file.UserId.Should().Be(user.Id);
        file.Source.Should().Be(FileSource.Usb);
        (await context.PrintJobs.Find(j => j.Id == jobId).FirstAsync()).FileId.Should().Be(fileId);

        var mine = await user.Client.GetFromJsonAsync<JsonElement>("/api/files");
        mine.GetProperty("items").EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == fileId);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient kiosk, Guid jobId)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("Salam, NeoPrint!\nFlaşdan gələn fayl.\n"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(content, "file", "qeyd.txt");

        return await kiosk.PostAsync($"/api/print-jobs/{jobId}/flash-upload", form);
    }
}
