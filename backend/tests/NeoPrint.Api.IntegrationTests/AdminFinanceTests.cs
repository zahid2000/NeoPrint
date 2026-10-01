using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The admin panel's money and customer pages (plan section 8A.3; docs/TASKS.md
/// T-110): payments with their split and history, the revenue report, the
/// dashboard card, vouchers, and blocking a customer with effect now.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminFinanceTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminFinanceTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_paid_order_shows_its_split_history_receipt_and_fiscal_part_and_counts_in_revenue()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "admin-money");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/payments?kioskId={kiosk.KioskId}");
        var row = list.GetProperty("items").EnumerateArray().Should().ContainSingle(p => p.GetProperty("id").GetGuid() == paymentId).Subject;
        row.GetProperty("status").GetString().Should().Be("Succeeded");
        row.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        row.GetProperty("amount").GetDecimal().Should().Be(price);

        // The six-digit job code in the search box finds it too.
        var jobCode = row.GetProperty("jobCode").GetString();
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/payments?search={jobCode}"))
            .GetProperty("items").EnumerateArray().Should().Contain(p => p.GetProperty("id").GetGuid() == paymentId);

        var detail = await EventuallyAsync(
            () => admin.GetFromJsonAsync<JsonElement>($"/api/admin/payments/{paymentId}"),
            d => d.TryGetProperty("receiptNumber", out var number) && number.ValueKind == JsonValueKind.String
                 && d.GetProperty("fiscal").EnumerateArray().Any(f => f.GetProperty("status").GetString() == "Issued"),
            "the receipt and the fiscal receipt");

        detail.GetProperty("split").GetProperty("external").GetDecimal().Should().Be(price);
        detail.GetProperty("split").GetProperty("gross").GetDecimal().Should().Be(price);
        detail.GetProperty("job").GetProperty("id").GetGuid().Should().Be(jobId);
        detail.GetProperty("timeline").EnumerateArray().Select(e => e.GetProperty("event").GetString()).Should().Contain("created");
        detail.GetProperty("refundable").GetDecimal().Should().Be(price);

        // The printer stops before anything came out: all of it goes back to the card.
        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Interrupted", errorCode: "printer.paperJam");

        var refunded = await EventuallyAsync(
            () => admin.GetFromJsonAsync<JsonElement>($"/api/admin/payments/{paymentId}"),
            d => d.GetProperty("refunds").EnumerateArray().Any(r => r.GetProperty("status").GetString() == "Succeeded"),
            "the refund");
        refunded.GetProperty("refunds").EnumerateArray().Single().GetProperty("destination").GetString().Should().Be("OriginalMethod", "the bank refunds the card that paid");

        var today = DateOnly.FromDateTime(NeoPrint.Application.Common.BakuTime.From(DateTime.UtcNow)).ToString("yyyy-MM-dd");
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/reports/revenue?from={today}&to={today}&kioskId={kiosk.KioskId}");
        report.GetProperty("totals").GetProperty("sales").GetDecimal().Should().Be(price);
        report.GetProperty("totals").GetProperty("refunds").GetDecimal().Should().Be(price);
        report.GetProperty("totals").GetProperty("net").GetDecimal().Should().Be(0);
        report.GetProperty("kiosks").EnumerateArray().Single().GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        report.GetProperty("methods").EnumerateArray().Single().GetProperty("method").GetString().Should().Be("BankQr");

        var dashboard = await admin.GetFromJsonAsync<JsonElement>("/api/admin/dashboard/payments");
        dashboard.GetProperty("lastSevenDays").GetArrayLength().Should().Be(7);

        (await admin.GetAsync("/api/admin/vouchers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/admin/reports/revenue?from={today}&to=2020-01-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_blocked_customer_is_out_at_once_keeps_the_wallet_and_comes_back_when_unblocked()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        var found = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users?search={Uri.EscapeDataString(user.Email[..12])}");
        found.GetProperty("items").EnumerateArray().Should().Contain(u => u.GetProperty("id").GetGuid() == user.Id);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users/{user.Id}");
        detail.GetProperty("profile").GetProperty("status").GetString().Should().Be("Active");
        detail.GetProperty("wallet").GetProperty("balance").GetDecimal().Should().Be(0);
        detail.GetProperty("consents").GetArrayLength().Should().Be(2);

        (await PostAsync(admin, $"/api/admin/users/{user.Id}/block", new { reason = "" })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a reason is required");

        var blocked = await PostAsync(admin, $"/api/admin/users/{user.Id}/block", new { reason = "Saxta kart şübhəsi" });
        blocked.StatusCode.Should().Be(HttpStatusCode.OK, await blocked.Content.ReadAsStringAsync());

        // The token still has minutes to run, and is refused now.
        (await user.Client.GetAsync("/api/wallet")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = user.RefreshToken })).IsSuccessStatusCode.Should().BeFalse();
        (await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = TestUsers.Password })).IsSuccessStatusCode.Should().BeFalse();

        // Blocking again is the same answer.
        (await PostAsync(admin, $"/api/admin/users/{user.Id}/block", new { reason = "Təkrar" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var unblocked = await PostAsync(admin, $"/api/admin/users/{user.Id}/unblock", new { reason = "Yoxlandı, problem yoxdur" });
        unblocked.StatusCode.Should().Be(HttpStatusCode.OK, await unblocked.Content.ReadAsStringAsync());

        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = TestUsers.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        (await user.Client.GetAsync("/api/wallet")).StatusCode.Should().Be(HttpStatusCode.OK, "the same token works again once the block is lifted");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }
}
