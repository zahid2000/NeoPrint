using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Payments;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Wallets;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The wallet and paying at a kiosk with the app (plan sections 2.14, 9.2;
/// docs/IDEMPOTENCY.md "double wallet credit"; docs/TASKS.md T-102).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WalletTests
{
    private readonly NeoPrintApiFixture _fixture;

    public WalletTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_top_up_lands_on_the_wallet_once_however_often_it_is_sent()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var key = Guid.NewGuid().ToString("N");

        var first = await TopUpAsync(user, 5m, key);
        var again = await TopUpAsync(user, 5m, key);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        again.StatusCode.Should().Be(HttpStatusCode.OK);

        (await BalanceAsync(user)).Should().Be(5m);
        (await LedgerAsync(user.Id)).Should().ContainSingle(entry => entry.Type == WalletTransactionType.TopUp);
    }

    [DockerFact]
    public async Task A_declined_card_puts_nothing_on_the_wallet()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);

        await WithCardOutcomeAsync("Decline", async () =>
        {
            var response = await TopUpAsync(user, 3m);
            var raw = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Conflict, raw);
            raw.Should().Contain("payments.declined");
        });

        (await BalanceAsync(user)).Should().Be(0m);
        (await LedgerAsync(user.Id)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_card_answer_lost_on_the_way_is_settled_later_once_not_charged_again()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);

        await WithCardOutcomeAsync("Timeout", async () =>
        {
            var response = await TopUpAsync(user, 4m);
            var raw = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, raw);
            raw.Should().Contain("payments.outcomeUnknown", "the customer is told not to pay again, not that nothing was taken");
        });

        (await BalanceAsync(user)).Should().Be(0m);

        using (var scope = _fixture.Services.CreateScope())
        {
            var topUp = await scope.ServiceProvider.GetRequiredService<MongoContext>().Payments
                .Find(p => p.UserId == user.Id && p.Purpose == PaymentPurpose.WalletTopUp)
                .SingleAsync();

            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            // The reconciliation job asks the acquirer; twice changes nothing more.
            (await sender.Send(new SettleCardPaymentCommand(topUp.Id))).Should().Be(SettleCardOutcome.Paid);
            (await sender.Send(new SettleCardPaymentCommand(topUp.Id))).Should().Be(SettleCardOutcome.Paid);
        }

        (await BalanceAsync(user)).Should().Be(4m);
        (await LedgerAsync(user.Id)).Should().ContainSingle();
    }

    [DockerFact]
    public async Task Paying_a_kiosk_order_from_the_wallet_queues_it_and_puts_it_on_the_account()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        (await TopUpAsync(user, 5m)).EnsureSuccessStatusCode();

        using var kiosk = await ReadyKioskAsync("app-wallet");
        var (jobId, paymentId, price) = await AppIntentAsync(kiosk);

        var view = await user.Client.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/app");
        view.GetProperty("amount").GetDecimal().Should().Be(price);
        view.GetProperty("walletBalance").GetDecimal().Should().Be(5m);
        view.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);

        var paid = await PayAsync(user, paymentId, "Wallet");
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
        (await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Succeeded");

        (await BalanceAsync(user)).Should().Be(5m - price);

        using var scope = _fixture.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<NeoPrint.Application.PrintJobs.IPrintJobRepository>().FindByIdAsync(jobId);
        job!.Status.Should().Be(PrintJobStatus.Queued);
        job.UserId.Should().Be(user.Id, "a job paid with the app belongs to that account (plan section 9.2, step 4)");
    }

    [DockerFact]
    public async Task The_same_app_payment_sent_twice_at_once_takes_the_money_once()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        (await TopUpAsync(user, 5m)).EnsureSuccessStatusCode();

        using var kiosk = await ReadyKioskAsync("app-twice");
        var (_, paymentId, price) = await AppIntentAsync(kiosk);

        var answers = await Task.WhenAll(PayAsync(user, paymentId, "Wallet"), PayAsync(user, paymentId, "Wallet"));

        answers.Count(a => a.StatusCode == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(1);
        (await BalanceAsync(user)).Should().Be(5m - price, "one order, one charge - whatever the phone resends");
        (await LedgerAsync(user.Id)).Count(entry => entry.Type == WalletTransactionType.Payment).Should().Be(1);
    }

    [DockerFact]
    public async Task A_wallet_that_cannot_cover_the_order_is_refused_and_nothing_is_set_aside()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var kiosk = await ReadyKioskAsync("app-empty");
        var (_, paymentId, _) = await AppIntentAsync(kiosk);

        var response = await PayAsync(user, paymentId, "Wallet");
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, raw);
        raw.Should().Contain("wallet.insufficient");

        using var scope = _fixture.Services.CreateScope();
        var payment = await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().FindByIdAsync(paymentId);
        payment!.Status.Should().Be(PaymentStatus.Pending, "the customer can still pay another way");
        payment.Reserved.Should().BeNull();
        payment.UserId.Should().BeNull("the whole attempt went back, including taking the payment over");
    }

    [DockerFact]
    public async Task A_declined_card_gives_the_bonus_pages_back_and_the_next_try_takes_them_again()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        (await AdjustAsync(admin, user.Id, 0m, 2, "Kompensasiya: test")).EnsureSuccessStatusCode();

        using var kiosk = await ReadyKioskAsync("app-bonus");
        var (_, paymentId, _) = await AppIntentAsync(kiosk);

        await WithCardOutcomeAsync("Decline", async () =>
        {
            var declined = await PayAsync(user, paymentId, "Card", useBonus: true);
            declined.StatusCode.Should().Be(HttpStatusCode.Conflict, await declined.Content.ReadAsStringAsync());
        });

        (await WalletAsync(user)).GetProperty("bonusPages").GetInt32().Should().Be(2, "the reservation came back");

        var paid = await PayAsync(user, paymentId, "Card", useBonus: true);
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        (await WalletAsync(user)).GetProperty("bonusPages").GetInt32().Should().Be(0);

        var ledger = await LedgerAsync(user.Id);
        ledger.Count(entry => entry.Type == WalletTransactionType.Payment).Should().Be(2, "one reservation per try");
        ledger.Where(entry => entry.Type == WalletTransactionType.Payment).Select(entry => entry.DedupKey).Should().OnlyHaveUniqueItems();
    }

    [DockerFact]
    public async Task An_admin_correction_needs_a_reason_is_audited_and_never_takes_the_balance_below_zero()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await AdjustAsync(admin, user.Id, 2m, 0, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var below = await AdjustAsync(admin, user.Id, -1m, 0, "Səhv düzəlişi");
        below.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await below.Content.ReadAsStringAsync()).Should().Contain("wallet.insufficient");

        (await AdjustAsync(admin, user.Id, 2m, 0, "Kompensasiya: kiosk dayandı")).EnsureSuccessStatusCode();
        (await BalanceAsync(user)).Should().Be(2m);

        using var scope = _fixture.Services.CreateScope();
        var audited = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(entry => entry.Action == "wallets.adjust" && entry.EntityId == user.Id.ToString())
            .CountDocumentsAsync();
        audited.Should().Be(1);
    }

    [DockerFact]
    public void No_endpoint_can_move_a_balance_out_of_the_wallet()
    {
        if (Skip)
        {
            return;
        }

        // Plan section 2.14: a balance is never paid back, cashed or moved to a
        // card - by the user or an admin. These are all the wallet routes there are.
        var routes = _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(route => route.Contains("wallet", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        routes.Should().BeEquivalentTo(
            "api/wallet",
            "api/wallet/transactions",
            "api/wallet/top-up",
            "api/admin/wallets/{userId:guid}",
            "api/admin/wallets/{userId:guid}/transactions",
            "api/admin/wallets/{userId:guid}/adjust");

        routes.Should().NotContain(route => route.Contains("withdraw") || route.Contains("payout") || route.Contains("cash"));
    }

    private async Task<TestKiosk> ReadyKioskAsync(string name)
    {
        var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"{name}-{Guid.NewGuid():N}"[..30]);

        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId),
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Update.Set("PaperStock.A4.Current", 500));

        return kiosk;
    }

    /// <summary>A flash order at the kiosk and its "pay with the NeoPrint app" QR.</summary>
    private static async Task<(Guid JobId, Guid PaymentId, decimal Price)> AppIntentAsync(TestKiosk kiosk)
    {
        using var place = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = 3,
                options = new { copies = 1, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = false,
            }),
        };
        place.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var placed = await kiosk.Client.SendAsync(place);
        placed.StatusCode.Should().Be(HttpStatusCode.Created, await placed.Content.ReadAsStringAsync());
        var job = await placed.Content.ReadFromJsonAsync<JsonElement>();

        using var open = new HttpRequestMessage(HttpMethod.Post, "/api/payments/intents")
        {
            Content = JsonContent.Create(new { jobId = job.GetProperty("id").GetGuid(), method = "MobileApp" }),
        };
        open.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var opened = await kiosk.Client.SendAsync(open);
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var intent = await opened.Content.ReadFromJsonAsync<JsonElement>();

        intent.GetProperty("qrContent").GetString().Should().Be($"neoprint://pay/{intent.GetProperty("id").GetGuid()}");

        return (job.GetProperty("id").GetGuid(), intent.GetProperty("id").GetGuid(), intent.GetProperty("amount").GetDecimal());
    }

    private static async Task<HttpResponseMessage> TopUpAsync(TestUser user, decimal amount, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount }) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));

        return await user.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PayAsync(TestUser user, Guid paymentId, string source, bool useBonus = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app")
        {
            Content = JsonContent.Create(new { source, useBonus }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await user.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> AdjustAsync(HttpClient admin, Guid userId, decimal amount, int bonusPages, string? reason)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/wallets/{userId}/adjust")
        {
            Content = JsonContent.Create(new { amount, bonusPages, reason }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<JsonElement> WalletAsync(TestUser user) => await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet");

    private static async Task<decimal> BalanceAsync(TestUser user) => (await WalletAsync(user)).GetProperty("balance").GetDecimal();

    private async Task<List<WalletTransaction>> LedgerAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<MongoContext>().WalletTransactions
            .Find(entry => entry.UserId == userId)
            .ToListAsync();
    }

    /// <summary>Sets how the mock card's charges end for the length of <paramref name="body"/>.</summary>
    private async Task WithCardOutcomeAsync(string outcome, Func<Task> body)
    {
        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PostAsync($"/api/dev/mock-card/outcome?value={outcome}", null)).EnsureSuccessStatusCode();

        try
        {
            await body();
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-card/outcome?value=Success", null)).EnsureSuccessStatusCode();
        }
    }
}
