using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Fiscal;
using NeoPrint.Contracts;
using NeoPrint.Domain.Fiscal;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Fiscal receipts (plan section 2.12; docs/IDEMPOTENCY.md "double fiscal";
/// docs/TASKS.md T-105): asynchronous, once per sale or refund, retried, an
/// incident when late, 0 AZN for bonus pages.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FiscalTests
{
    private readonly NeoPrintApiFixture _fixture;

    public FiscalTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_kiosk_sale_gets_one_receipt_through_the_kiosks_till_however_often_it_is_told()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-sale");
        var (_, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);

        var sale = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r?.Status == FiscalReceiptStatus.Issued, "the sale receipt");
        sale!.Type.Should().Be(FiscalReceiptType.Sale);
        sale.TerminalId.Should().Be(TestTerminal(kiosk));
        sale.Amount.Amount.Should().Be(price);
        sale.PaidWith.Should().ContainSingle().Which.Kind.Should().Be("BankQr");
        sale.FiscalDocumentNumber.Should().NotBeNullOrEmpty();
        sale.VerificationUrl.Should().Contain(sale.FiscalId!);

        // The event again, twice at once, and once more: still one receipt, one document.
        var again = new PaymentSucceeded(paymentId, null, kiosk.KioskId, null, price, "BankQr", true, null);
        await Task.WhenAll(FiscalizeAsync(again), FiscalizeAsync(again));
        await FiscalizeAsync(again);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        (await context.FiscalReceipts.Find(r => r.PaymentId == paymentId && r.Type == FiscalReceiptType.Sale).CountDocumentsAsync()).Should().Be(1);
        (await context.MockFiscalDocuments.Find(d => d.Id == sale.Id.ToString("N")).CountDocumentsAsync()).Should().Be(1, "no double fiscal");

        using var anonymous = _fixture.CreateApiClient();
        var fiscal = await anonymous.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}/fiscal");
        fiscal.GetProperty("status").GetString().Should().Be("Issued");
        fiscal.GetProperty("verificationUrl").GetString().Should().Be(sale.VerificationUrl);
    }

    [DockerFact]
    public async Task An_e_kassa_that_is_down_is_retried_an_incident_comes_when_late_and_it_closes_when_issued()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-down");
        using var anonymous = _fixture.CreateApiClient();
        FiscalReceipt pending;

        (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Down", null)).EnsureSuccessStatusCode();

        try
        {
            var (_, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
            pending = (await EventuallyAsync(
                // The try is counted before the e-kassa is called and its outcome
                // saved after: wait for the outcome, not just the count (T-151).
                () => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r is { Attempts: > 0, LastError: not null }, "the first try"))!;

            pending.Status.Should().Be(FiscalReceiptStatus.Pending, "the payment never waits for the e-kassa");
            pending.LastError.Should().Be("fiscal.provider.unavailable");
            pending.NextAttemptAt.Should().BeAfter(DateTime.UtcNow);

            // T-127: the payment page reads the kept error in the admin's language, not as its key.
            using var admin = await TestKiosks.SuperAdminAsync(_fixture);
            using var detail = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/{paymentId}");
            detail.Headers.AcceptLanguage.ParseAdd("az");
            var page = await (await admin.SendAsync(detail)).Content.ReadFromJsonAsync<JsonElement>();
            page.GetProperty("fiscal")[0].GetProperty("lastError").GetString()
                .Should().Be("E-kassa cavab vermədi; çek avtomatik yenidən göndəriləcək.");

            // Older than Fiscal.MaxPendingMinutes: one incident, however often it is checked.
            await AgeAsync(pending.Id, minutes: 30);
            await SendAsync(new ReportLateFiscalReceiptsCommand());
            await SendAsync(new ReportLateFiscalReceiptsCommand());
            (await IncidentsAsync(pending.Id, open: true)).Should().ContainSingle(i => i.Type == IncidentType.FiscalFailed && i.Severity == IncidentSeverity.High)
                .Which.Details.Should().Contain("causeMessage=fiscal.provider.unavailable", "the cause is kept as its key (T-140)").And.NotContain("E-kassa");

            // The same incident in English for an English-speaking admin.
            using var english = new HttpRequestMessage(HttpMethod.Get, "/api/admin/incidents?status=Open&type=FiscalFailed&pageSize=100");
            english.Headers.AcceptLanguage.ParseAdd("en");
            var incidents = await (await admin.SendAsync(english)).Content.ReadFromJsonAsync<JsonElement>();
            incidents.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("entityId").GetString() == pending.Id.ToString())
                .GetProperty("details").GetString()
                .Should().Contain("Reason: The e-kassa did not answer; the receipt is sent again automatically. Payments");
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Ok", null)).EnsureSuccessStatusCode();
        }

        // Back up: the next due try issues it and the incident closes.
        await SendAsync(new IssueFiscalReceiptCommand(pending.Id));
        (await ReceiptByIdAsync(pending.Id)).Status.Should().Be(FiscalReceiptStatus.Issued);
        (await IncidentsAsync(pending.Id, open: true)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_refused_receipt_fails_raises_an_incident_and_the_admin_retries_it()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-reject");
        using var anonymous = _fixture.CreateApiClient();
        Guid receiptId;

        (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Reject", null)).EnsureSuccessStatusCode();

        try
        {
            var (_, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
            var failed = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r?.Status == FiscalReceiptStatus.Failed, "the refusal");
            receiptId = failed!.Id;
            (await IncidentsAsync(receiptId, open: true)).Should().ContainSingle(i => i.DedupKey == $"fiscal-failed:{receiptId}");
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-fiscal/mode?mode=Ok", null)).EnsureSuccessStatusCode();
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/fiscal-receipts?status=Failed&type=Sale");
        list.GetProperty("items").EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == receiptId);

        // The panel sees the incident next to the receipt, in its own words (T-129).
        var open = await OpenFiscalIncidentAsync(admin, receiptId, "Open");
        open.GetProperty("entityType").GetString().Should().Be("FiscalReceipt");
        open.GetProperty("severity").GetString().Should().Be("High");
        open.GetProperty("details").GetString().Should().StartWith("E-kassa").And.NotContain("incidents.");
        (await admin.GetAsync("/api/admin/incidents?status=Nope")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/fiscal-receipts/{receiptId}/retry");
        retry.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var retried = await admin.SendAsync(retry);
        retried.StatusCode.Should().Be(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        (await retried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Issued");
        (await IncidentsAsync(receiptId, open: true)).Should().BeEmpty();
        (await OpenFiscalIncidentAsync(admin, receiptId, "Resolved")).GetProperty("resolution").GetString().Should().StartWith("Fiskal çek verildi");

        using var again = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/fiscal-receipts/{receiptId}/retry");
        again.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await admin.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Conflict, "an issued receipt is not sent twice");
    }

    [DockerFact]
    public async Task A_refund_receipt_names_the_sale_and_waits_for_it()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-refund");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);
        var sale = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r?.Status == FiscalReceiptStatus.Issued, "the sale receipt");

        // The printer stops before anything came out: scenario A refunds it all to the card.
        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Interrupted", errorCode: "printer.paperJam");

        var receipts = await EventuallyAsync(
            () => ReceiptsForAsync(paymentId), r => r.Any(x => x.Type == FiscalReceiptType.Refund && x.Status == FiscalReceiptStatus.Issued), "the refund receipt");

        var refund = receipts.Single(r => r.Type == FiscalReceiptType.Refund);
        refund.Amount.Amount.Should().Be(price);
        refund.OriginalFiscalId.Should().Be(sale!.FiscalId);
        refund.TerminalId.Should().Be(sale.TerminalId, "the refund goes through the till that made the sale");
        receipts.Count(r => r.Type == FiscalReceiptType.Refund).Should().Be(1);
    }

    [DockerFact]
    public async Task A_kiosk_without_a_till_waits_and_never_borrows_another()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-noterm");

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
                Builders<Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId), Builders<Kiosk>.Update.Set(k => k.FiscalTerminalId, null));
        }

        var (_, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
        var receipt = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r is { Attempts: > 0 }, "the first try");

        receipt!.Status.Should().Be(FiscalReceiptStatus.Pending);
        receipt.TerminalId.Should().BeNull();
        receipt.LastError.Should().Be("fiscal.noTerminal");
    }

    [DockerFact]
    public async Task A_top_up_is_an_advance_receipt_and_the_wallet_sale_says_it_was_paid_in_advance()
    {
        if (Skip)
        {
            return;
        }

        await SetAsync("Fiscal.OnlineTerminalId", "ONLINE-TEST");

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using (var topUp = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount = 5m }) })
        {
            topUp.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await user.Client.SendAsync(topUp)).EnsureSuccessStatusCode();
        }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var topUpPayment = await context.Payments.Find(p => p.UserId == user.Id).FirstAsync();

        var advance = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.TopUpKey(topUpPayment.Id)), r => r?.Status == FiscalReceiptStatus.Issued, "the top-up receipt");
        advance!.Type.Should().Be(FiscalReceiptType.TopUp);
        advance.TerminalId.Should().Be("ONLINE-TEST", "an app payment goes through the online till");
        advance.Amount.Amount.Should().Be(5m);

        // A kiosk order paid from the wallet: a sale paid in advance.
        using var kiosk = await ReadyKioskAsync(_fixture, "fiscal-wallet");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, job.GetProperty("id").GetGuid(), "MobileApp");
        var paymentId = intent.GetProperty("id").GetGuid();

        using (var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app") { Content = JsonContent.Create(new { source = "Wallet", useBonus = false }) })
        {
            pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await user.Client.SendAsync(pay)).EnsureSuccessStatusCode();
        }

        var sale = await EventuallyAsync(() => ReceiptAsync(FiscalizationHandlers.SaleKey(paymentId)), r => r?.Status == FiscalReceiptStatus.Issued, "the wallet sale receipt");
        sale!.PaidWith.Should().ContainSingle().Which.Kind.Should().Be("Advance");
        sale.TerminalId.Should().Be(TestTerminal(kiosk));
    }

    [DockerFact]
    public async Task Only_fiscal_settings_change_on_the_fiscal_page()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/admin/fiscal/settings");
        settings.EnumerateArray().Select(s => s.GetProperty("key").GetString()).Should().OnlyContain(key => key!.StartsWith("Fiscal.", StringComparison.Ordinal));

        var refused = await admin.PutAsJsonAsync("/api/admin/fiscal/settings", new { values = new Dictionary<string, string> { ["Payment.TimeoutMinutes"] = "1" } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var saved = await admin.PutAsJsonAsync("/api/admin/fiscal/settings", new { values = new Dictionary<string, string> { ["Fiscal.MaxPendingMinutes"] = "20" } });
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        await SetAsync("Fiscal.MaxPendingMinutes", "15");
    }

    private static string TestTerminal(TestKiosk kiosk) => $"TERM-{kiosk.KioskId.ToString("N")[..8]}";

    private async Task FiscalizeAsync(PaymentSucceeded payment)
    {
        try
        {
            await SendAsync(new FiscalizePaymentCommand(payment));
        }
        catch (MongoException)
        {
            // The racing twin lost at the unique index: exactly what should happen.
        }
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task SetAsync(string key, string value)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsStore>().UpdateAsync(new Dictionary<string, string> { [key] = value }, updatedBy: null);
    }

    private async Task AgeAsync(Guid receiptId, int minutes)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().FiscalReceipts.UpdateOneAsync(
            Builders<FiscalReceipt>.Filter.Eq(r => r.Id, receiptId),
            Builders<FiscalReceipt>.Update.Set(r => r.CreatedAt, DateTime.UtcNow.AddMinutes(-minutes)).Set(r => r.NextAttemptAt, DateTime.UtcNow));
    }

    private async Task<FiscalReceipt?> ReceiptAsync(string dedupKey)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFiscalReceiptRepository>().FindByDedupKeyAsync(dedupKey);
    }

    private async Task<FiscalReceipt> ReceiptByIdAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IFiscalReceiptRepository>().FindByIdAsync(id))!;
    }

    private async Task<IReadOnlyList<FiscalReceipt>> ReceiptsForAsync(Guid paymentId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFiscalReceiptRepository>().ListForPaymentAsync(paymentId);
    }

    private static async Task<JsonElement> OpenFiscalIncidentAsync(HttpClient admin, Guid receiptId, string status)
    {
        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/incidents?status={status}&type=FiscalFailed&pageSize=100");
        return page.GetProperty("items").EnumerateArray().Should()
            .ContainSingle(item => item.GetProperty("entityId").GetString() == receiptId.ToString()).Subject;
    }

    private async Task<List<Incident>> IncidentsAsync(Guid receiptId, bool open)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
            .Find(i => i.EntityId == receiptId && i.Open == open).ToListAsync();
    }
}
