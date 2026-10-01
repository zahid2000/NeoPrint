using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Fiscal;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Promos;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Promo codes (plan section 2.7; docs/TASKS.md T-107): applied last, after
/// bonus pages; limits that hold under parallel payments; a usage given back
/// when the payment does not go through and cancelled when it is refunded.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PromoTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PromoTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_kiosk_order_with_a_code_is_paid_at_the_discounted_price_and_the_receipts_say_so()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Percent", 50m);

        (await CreateRawAsync(admin, code, "Percent", 10m)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the code is taken");
        (await CreateRawAsync(admin, NewCode(), "Percent", 150m)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var kiosk = await ReadyKioskAsync(_fixture, "promo-kiosk");

        // The quote and the check show it before anything is ordered.
        var check = await Post<JsonElement>(kiosk.Client, "/api/promos/validate", new { code = code.ToLowerInvariant(), kioskId = kiosk.KioskId, amount = 0.30m });
        check.GetProperty("valid").GetBoolean().Should().BeTrue();
        check.GetProperty("discount").GetDecimal().Should().Be(0.15m);

        var unknown = await Post<JsonElement>(kiosk.Client, "/api/promos/validate", new { code = "NOPE-000", kioskId = kiosk.KioskId, amount = 0.30m });
        unknown.GetProperty("valid").GetBoolean().Should().BeFalse();
        unknown.GetProperty("reasonCode").GetString().Should().Be("promos.notFound");

        // A code that does not apply is refused with the order, before paying.
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "a.pdf", pageCount = 3, promoCode = "NOPE-000",
                options = new { copies = 1, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
            }),
        })
        {
            bad.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await kiosk.Client.SendAsync(bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var job = await PlaceAsync(kiosk.Client, 3, code);
        job.GetProperty("price").GetProperty("promoDiscount").GetDecimal().Should().Be(0.15m);
        job.GetProperty("price").GetProperty("amountDue").GetDecimal().Should().Be(0.15m);

        var intent = await IntentAsync(kiosk.Client, job.GetProperty("id").GetGuid());
        intent.GetProperty("amount").GetDecimal().Should().Be(0.15m, "the bank is asked for the discounted price");
        intent.GetProperty("discount").GetDecimal().Should().Be(0.15m);
        var paymentId = intent.GetProperty("id").GetGuid();

        (await SimulateAsync(_fixture, paymentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var usage = await EventuallyAsync(() => UsageAsync(paymentId), u => u?.Status == PromoUsageStatus.Confirmed, "the confirmed usage");
        usage!.Discount.Amount.Should().Be(0.15m);
        (await PromoAsync(code)).UsedCount.Should().Be(1);

        var sale = await EventuallyAsync(() => FiscalAsync(paymentId), r => r?.Status == FiscalReceiptStatus.Issued, "the sale receipt");
        sale!.Amount.Amount.Should().Be(0.15m, "only what was paid is fiscalized");

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/promos?code={code}");
        var item = view.GetProperty("items")[0];
        item.GetProperty("confirmedCount").GetInt64().Should().Be(1);
        item.GetProperty("totalDiscount").GetDecimal().Should().Be(0.15m);
    }

    [DockerFact]
    public async Task The_last_place_goes_to_one_payment_and_the_other_pays_the_full_price_knowing_why()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Fixed", 0.10m, totalLimit: 1);
        using var kiosk = await ReadyKioskAsync(_fixture, "promo-race");

        var first = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var second = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();

        var intents = await Task.WhenAll(IntentAsync(kiosk.Client, first), IntentAsync(kiosk.Client, second));

        intents.Count(i => i.GetProperty("discount").GetDecimal() == 0.10m).Should().Be(1, "one place, one discount");
        var full = intents.Single(i => i.GetProperty("discount").GetDecimal() == 0);
        full.GetProperty("amount").GetDecimal().Should().Be(0.30m);
        full.GetProperty("promoRefusal").GetString().Should().NotBeNullOrEmpty();
        (await PromoAsync(code)).UsedCount.Should().Be(1);
    }

    [DockerFact]
    public async Task A_payment_that_does_not_go_through_gives_the_place_back_and_a_refunded_one_cancels_it()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Percent", 50m, totalLimit: 1);
        using var kiosk = await ReadyKioskAsync(_fixture, "promo-back");

        // Switching to the app closes the bank intent: its place goes back.
        var jobId = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var bank = await IntentAsync(kiosk.Client, jobId);
        (await PromoAsync(code)).UsedCount.Should().Be(1);

        await IntentAsync(kiosk.Client, jobId, "MobileApp");
        (await PromoAsync(code)).UsedCount.Should().Be(0);
        (await UsageAsync(bank.GetProperty("id").GetGuid()))!.Status.Should().Be(PromoUsageStatus.Released);

        // Paid, then refunded in full: the use is cancelled; the discount was never money, so it is not paid out.
        var paidJob = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var paid = await IntentAsync(kiosk.Client, paidJob);
        var paymentId = paid.GetProperty("id").GetGuid();
        (await SimulateAsync(_fixture, paymentId)).StatusCode.Should().Be(HttpStatusCode.OK);
        await EventuallyAsync(() => UsageAsync(paymentId), u => u?.Status == PromoUsageStatus.Confirmed, "the confirmed usage");

        using (var refund = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/{paymentId}/refund")
        {
            Content = JsonContent.Create(new { amount = (decimal?)null, destination = (string?)null, reason = "Test", cancelJob = true }),
        })
        {
            refund.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await admin.SendAsync(refund)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var payment = await EventuallyAsync(() => PaymentAsync(paymentId), p => p.Status == PaymentStatus.Refunded, "the refund");
        payment.RefundedAmount.Amount.Should().Be(0.15m, "what was paid, not the price before the code");
        (await UsageAsync(paymentId))!.Status.Should().Be(PromoUsageStatus.Cancelled);
        (await PromoAsync(code)).UsedCount.Should().Be(0);
    }

    [DockerFact]
    public async Task In_the_app_the_code_applies_after_bonus_pages_and_once_per_account()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Percent", 50m, perUserLimit: 1);
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        await AdjustAsync(admin, user.Id, 5m, 1);

        using var kiosk = await ReadyKioskAsync(_fixture, "promo-app");
        var jobId = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var paymentId = (await IntentAsync(kiosk.Client, jobId, "MobileApp")).GetProperty("id").GetGuid();

        var view = await user.Client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/app");
        view.GetProperty("amount").GetDecimal().Should().Be(0.30m);
        view.GetProperty("promoDiscount").GetDecimal().Should().Be(0.15m);
        view.GetProperty("promoDiscountWithBonus").GetDecimal().Should().Be(0.10m, "one bonus side (0,10) first, then half of the 0,20 left");

        (await PayAsync(user, paymentId, useBonus: true, expected: 0.15m)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the app's figure is checked");
        var paid = await PayAsync(user, paymentId, useBonus: true, expected: 0.10m);
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var payment = await PaymentAsync(paymentId);
        payment.Discount.Amount.Should().Be(0.10m);
        payment.Parts!.BonusPages.Should().Be(1);
        payment.Parts.Wallet.Amount.Should().Be(0.10m);

        // The same account again: the preview says why, and the full price is taken - as shown.
        var again = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var secondPayment = (await IntentAsync(kiosk.Client, again, "MobileApp")).GetProperty("id").GetGuid();
        var secondView = await user.Client.GetFromJsonAsync<JsonElement>($"/api/payments/{secondPayment}/app");
        secondView.GetProperty("promoDiscount").GetDecimal().Should().Be(0);
        secondView.GetProperty("promoRefusal").GetString().Should().NotBeNullOrEmpty();

        (await PayAsync(user, secondPayment, useBonus: false, expected: 0.30m)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PaymentAsync(secondPayment)).Discount.Amount.Should().Be(0);
    }

    [DockerFact]
    public async Task A_code_that_covers_everything_is_paid_without_the_bank()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Percent", 100m);
        using var kiosk = await ReadyKioskAsync(_fixture, "promo-free");
        var jobId = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();

        var intent = await IntentAsync(kiosk.Client, jobId);
        intent.GetProperty("status").GetString().Should().Be("Succeeded");
        intent.GetProperty("amount").GetDecimal().Should().Be(0);
        intent.TryGetProperty("qrContent", out _).Should().BeFalse("nothing to pay, nothing to scan");
        (await ReceiptExistsAsync(intent.GetProperty("id").GetGuid())).Should().BeTrue();
        (await FiscalAsync(intent.GetProperty("id").GetGuid())).Should().BeNull("nothing paid in money is not fiscalized");
    }

    [DockerFact]
    public async Task A_code_typed_in_the_app_applies_when_the_kiosk_order_has_none_and_a_wrong_one_takes_nothing()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var code = await CreateAsync(admin, "Percent", 50m);
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        await AdjustAsync(admin, user.Id, 5m, 0);

        using var kiosk = await ReadyKioskAsync(_fixture, "promo-typed");
        var jobId = (await PlaceAsync(kiosk.Client, 3)).GetProperty("id").GetGuid();
        var paymentId = (await IntentAsync(kiosk.Client, jobId, "MobileApp")).GetProperty("id").GetGuid();

        // A code that does not exist: refused on the field, nothing set aside.
        var wrong = await PayAsync(user, paymentId, useBonus: false, expected: 0.15m, promoCode: "NOPE-1234");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("promoCode", out _).Should().BeTrue();
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet")).GetProperty("balance").GetDecimal().Should().Be(5m);

        // The real one: half of 0,30.
        var paid = await PayAsync(user, paymentId, useBonus: false, expected: 0.15m, promoCode: code.ToLowerInvariant());
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var payment = await PaymentAsync(paymentId);
        payment.PromoCode.Should().Be(code);
        payment.Discount.Amount.Should().Be(0.15m);
        payment.Parts!.Wallet.Amount.Should().Be(0.15m);
        await EventuallyAsync(() => UsageAsync(paymentId), u => u?.Status == PromoUsageStatus.Confirmed, "the confirmed usage");

        // A kiosk order that already has a code keeps it: one code per order.
        var other = await CreateAsync(admin, "Fixed", 0.05m);
        var withCode = (await PlaceAsync(kiosk.Client, 3, code)).GetProperty("id").GetGuid();
        var second = (await IntentAsync(kiosk.Client, withCode, "MobileApp")).GetProperty("id").GetGuid();
        (await PayAsync(user, second, useBonus: false, expected: 0.15m, promoCode: other)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PaymentAsync(second)).PromoCode.Should().Be(code);
    }

    private static string NewCode() => $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private static async Task<string> CreateAsync(HttpClient admin, string kind, decimal value, int? totalLimit = null, int? perUserLimit = null)
    {
        var code = NewCode();
        var response = await CreateRawAsync(admin, code, kind, value, totalLimit, perUserLimit);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        return code;
    }

    private static async Task<HttpResponseMessage> CreateRawAsync(HttpClient admin, string code, string kind, decimal value, int? totalLimit = null, int? perUserLimit = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/promos")
        {
            Content = JsonContent.Create(new { code, kind, value, totalLimit, perUserLimit, minAmount = 0m }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task AdjustAsync(HttpClient admin, Guid userId, decimal amount, int bonusPages)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/wallets/{userId}/adjust")
        {
            Content = JsonContent.Create(new { amount, bonusPages, reason = "Test: balans və bonus" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await admin.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<HttpResponseMessage> PayAsync(TestUser user, Guid paymentId, bool useBonus, decimal expected, string? promoCode = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Wallet", useBonus, expectedAmount = expected, promoCode }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await user.Client.SendAsync(request);
    }

    private async Task<PromoCode> PromoAsync(string code)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().PromoCodes.Find(p => p.Code == code).FirstAsync();
    }

    private async Task<PromoUsage?> UsageAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().PromoUsages.Find(u => u.PaymentId == paymentId).FirstOrDefaultAsync();
    }

    private async Task<Payment> PaymentAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().FindByIdAsync(paymentId))!;
    }

    private async Task<FiscalReceipt?> FiscalAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFiscalReceiptRepository>().FindByDedupKeyAsync(FiscalizationHandlers.SaleKey(paymentId));
    }

    private async Task<bool> ReceiptExistsAsync(Guid paymentId)
    {
        await EventuallyAsync(
            async () =>
            {
                using var scope = _fixture.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MongoContext>().Receipts.Find(r => r.Id == paymentId).AnyAsync();
            },
            exists => exists,
            "the receipt");

        return true;
    }
}
