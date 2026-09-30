using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Application.Refunds;
using NeoPrint.Contracts;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Refunds (plan section 2.5; docs/IDEMPOTENCY.md "double refund"; the
/// owner's evidence rule of 2026-09-30; docs/TASKS.md T-103).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RefundTests
{
    private readonly NeoPrintApiFixture _fixture;

    public RefundTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_bank_paid_job_the_printer_stopped_is_refunded_to_the_card_for_what_did_not_print()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-a");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk, pages: 3);
        price.Should().Be(0.30m);

        var attempt = new { number = 1, printer = "Fake", spoolerJobId = "F-1", startedAt = DateTime.UtcNow, finishedAt = DateTime.UtcNow, counterBefore = 100L, counterAfter = 101L, counterUnit = "impressions", recovered = false };

        await ReportAsync(kiosk.Client, jobId, "Printing", attempt: attempt);

        // The printer's counter says one side came out, the kiosk thinks two:
        // the customer gets the benefit of the doubt.
        await ReportAsync(kiosk.Client, jobId, "Interrupted", sides: 2, sheets: 2, errorCode: "printer.paperJam", attempt: attempt);

        var refund = await EventuallyAsync(() => RefundsForAsync(paymentId), r => r.Any(x => x.Status == RefundStatus.Succeeded), "the automatic refund");

        var single = refund.Should().ContainSingle().Subject;
        single.Destination.Should().Be(RefundDestination.OriginalMethod);
        single.Reason.Should().Be(RefundReason.PartialPrint);
        single.Amount.Amount.Should().Be(0.20m, "two of three sides did not print");
        single.Evidence!.Source.Should().Be(EvidenceSource.PrinterCounter);
        single.Evidence.PrintedSides.Should().Be(1);
        single.Evidence.Detail.Should().Contain("KioskJournal=2");

        (await JobAsync(jobId)).Status.Should().Be(PrintJobStatus.Cancelled);
        var payment = await PaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        payment.RefundedAmount.Amount.Should().Be(0.20m);

        // What the anonymous customer sees behind the QR.
        using var anonymous = _fixture.CreateApiClient();
        var status = await anonymous.GetFromJsonAsync<JsonElement>($"/api/refunds/{single.Id}/public");
        status.GetProperty("status").GetString().Should().Be("Succeeded");
        status.GetProperty("destination").GetString().Should().Be("Card");
    }

    [DockerFact]
    public async Task The_same_refund_decision_twice_at_once_gives_the_money_back_once()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-twice");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);
        var decision = new RefundRequested(paymentId, jobId, "SystemError", null, $"test:{Guid.NewGuid():N}", null);

        await Task.WhenAll(CreateAsync(decision), CreateAsync(decision));
        await CreateAsync(decision);

        var refunds = await RefundsForAsync(paymentId);
        refunds.Should().ContainSingle("one decision, one refund");

        await Task.WhenAll(ProcessAsync(refunds[0].Id), ProcessAsync(refunds[0].Id));
        await ProcessAsync(refunds[0].Id);

        var payment = await PaymentAsync(paymentId);
        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.RefundedAmount.Amount.Should().Be(price, "paid back once, not twice");

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<MongoContext>().MockRefunds.Find(r => r.ProviderRef == payment.ProviderRef).CountDocumentsAsync())
            .Should().Be(1, "the bank was asked with one key");
    }

    [DockerFact]
    public async Task Refunds_never_add_up_to_more_than_the_payment()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-limit");
        var (_, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await AdminRefundAsync(admin, paymentId, 0.10m)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AdminRefundAsync(admin, paymentId, price)).StatusCode.Should().Be(HttpStatusCode.Conflict, "only 0,20 is left");
        (await AdminRefundAsync(admin, paymentId, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await EventuallyAsync(() => PaymentAsync(paymentId), p => p.Status == PaymentStatus.Refunded, "both refunds");

        var nothing = await AdminRefundAsync(admin, paymentId, null);
        nothing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await nothing.Content.ReadAsStringAsync()).Should().Contain("refunds.nothingLeft");

        (await PaymentAsync(paymentId)).RefundedAmount.Amount.Should().Be(price);
    }

    [DockerFact]
    public async Task A_refund_the_bank_keeps_refusing_fails_raises_an_incident_and_an_admin_sends_it_to_a_voucher()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-fail");
        var (jobId, paymentId, price) = await PaidBankJobAsync(_fixture, kiosk);
        using var anonymous = _fixture.CreateApiClient();
        using var scope = _fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await settings.UpdateAsync(new Dictionary<string, string> { ["Refund.ProviderMaxAttempts"] = "1" }, updatedBy: null, CancellationToken.None);
        (await anonymous.PostAsync("/api/dev/mock-bank/refunds-fail?fails=true", null)).EnsureSuccessStatusCode();

        try
        {
            // The customer cancels a queued job by its code at the kiosk.
            using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/{jobId}/cancel");
            cancel.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await kiosk.Client.SendAsync(cancel)).StatusCode.Should().Be(HttpStatusCode.OK);

            List<Refund> failed;

            try
            {
                failed = await EventuallyAsync(() => RefundsForAsync(paymentId), r => r.Any(x => x.Status == RefundStatus.Failed), "the failed refund");
            }
            catch (TimeoutException exception)
            {
                var seen = await RefundsForAsync(paymentId);
                throw new TimeoutException(
                    $"{exception.Message} Job {(await JobAsync(jobId)).Status}, payment {(await PaymentAsync(paymentId)).Status}, refunds: "
                    + string.Join("; ", seen.Select(r => $"{r.Status} tries={r.Attempts} next={r.NextAttemptAt:O} error={r.LastError}")),
                    exception);
            }
            var refund = failed.Single();

            refund.Reason.Should().Be(RefundReason.UserCancel);
            refund.LastError.Should().Be("refunds.provider.bankRefused");
            (await PaymentAsync(paymentId)).Status.Should().Be(PaymentStatus.RefundFailed);

            var incidents = await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
                .Find(i => i.EntityId == refund.Id && i.Type == IncidentType.RefundFailed).CountDocumentsAsync();
            incidents.Should().Be(1);

            // An anonymous card cannot take it; the admin gives a voucher instead.
            using var admin = await TestKiosks.SuperAdminAsync(_fixture);
            var retry = await RetryAsync(admin, refund.Id, "Voucher");
            retry.StatusCode.Should().Be(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());

            var done = await retry.Content.ReadFromJsonAsync<JsonElement>();
            done.GetProperty("status").GetString().Should().Be("Succeeded");
            var code = done.GetProperty("voucherCode").GetString()!;
            code.Should().MatchRegex("^RF-[A-Z2-9]{4}-[A-Z2-9]{4}$");

            (await RetryAsync(admin, refund.Id, "Wallet")).StatusCode.Should().Be(HttpStatusCode.Conflict, "a succeeded refund is not retried");

            // Later the customer signs up and puts the voucher on their wallet - once.
            using var user = await TestUsers.ConfirmedAsync(_fixture);
            (await RedeemAsync(user, code)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await RedeemAsync(user, code)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet")).GetProperty("balance").GetDecimal().Should().Be(price);
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-bank/refunds-fail?fails=false", null)).EnsureSuccessStatusCode();
            await settings.UpdateAsync(new Dictionary<string, string> { ["Refund.ProviderMaxAttempts"] = "3" }, updatedBy: null, CancellationToken.None);
        }
    }

    [DockerFact]
    public async Task A_failed_try_waits_its_turn_and_the_next_one_succeeds()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-retry");
        var (jobId, paymentId, _) = await PaidBankJobAsync(_fixture, kiosk);
        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PostAsync("/api/dev/mock-bank/refunds-fail?fails=true", null)).EnsureSuccessStatusCode();

        Refund refund;

        try
        {
            await CreateAsync(new RefundRequested(paymentId, jobId, "SystemError", null, $"test:{Guid.NewGuid():N}", null));
            refund = (await RefundsForAsync(paymentId)).Single();
            await ProcessAsync(refund.Id);

            refund = (await RefundsForAsync(paymentId)).Single();
            refund.Status.Should().Be(RefundStatus.Processing, "one failure of three is not the end");
            refund.Attempts.Should().Be(1);
            refund.NextAttemptAt.Should().BeAfter(DateTime.UtcNow.AddSeconds(30));

            // Not due yet: asking again does not try again.
            await ProcessAsync(refund.Id);
            (await RefundsForAsync(paymentId)).Single().Attempts.Should().Be(1);
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-bank/refunds-fail?fails=false", null)).EnsureSuccessStatusCode();
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Refunds.UpdateOneAsync(
                r => r.Id == refund.Id, Builders<Refund>.Update.Set(r => r.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        await ProcessAsync(refund.Id);

        var done = (await RefundsForAsync(paymentId)).Single();
        done.Status.Should().Be(RefundStatus.Succeeded);
        done.Attempts.Should().Be(2);
    }

    [DockerFact]
    public async Task Cancelling_an_unpaid_job_closes_its_qr_so_it_can_no_longer_be_paid()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-close");
        var job = await PlaceAsync(kiosk.Client);
        var jobId = job.GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, jobId);
        var paymentId = intent.GetProperty("id").GetGuid();

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/{jobId}/cancel");
        cancel.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await kiosk.Client.SendAsync(cancel)).StatusCode.Should().Be(HttpStatusCode.OK);

        await EventuallyAsync(() => PaymentAsync(paymentId), p => p.Status == PaymentStatus.Expired, "the intent closing");

        (await SimulateAsync(_fixture, paymentId)).StatusCode.Should().Be(HttpStatusCode.Conflict, "the bank refuses a closed QR");
        (await RefundsForAsync(paymentId)).Should().BeEmpty("nothing was paid, nothing goes back");
    }

    [DockerFact]
    public async Task A_wallet_payment_goes_back_only_to_the_wallet_never_to_a_card()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var topUp = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount = 5m }) };
        topUp.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(topUp)).EnsureSuccessStatusCode();

        using var kiosk = await ReadyKioskAsync(_fixture, "refund-wallet");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, job.GetProperty("id").GetGuid(), "MobileApp");
        var paymentId = intent.GetProperty("id").GetGuid();
        var price = intent.GetProperty("amount").GetDecimal();

        using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app") { Content = JsonContent.Create(new { source = "Wallet", useBonus = false }) };
        pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(pay)).EnsureSuccessStatusCode();

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var options = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/payments/{paymentId}/refund-options");
        options.GetProperty("externalDestinations").GetArrayLength().Should().Be(0, "nothing of it came from a card");
        options.GetProperty("walletRefundable").GetDecimal().Should().Be(price);

        (await AdminRefundAsync(admin, paymentId, null, "OriginalMethod")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AdminRefundAsync(admin, paymentId, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await EventuallyAsync(() => PaymentAsync(paymentId), p => p.Status == PaymentStatus.Refunded, "the wallet refund");
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet")).GetProperty("balance").GetDecimal().Should().Be(5m);
    }

    private async Task CreateAsync(RefundRequested decision)
    {
        using var scope = _fixture.Services.CreateScope();

        try
        {
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateRefundsCommand(decision));
        }
        catch (Exception exception) when (exception is DuplicateRefundException or MongoDB.Driver.MongoException)
        {
            // The racing twin lost at the unique index: exactly what should happen.
        }
    }

    private async Task ProcessAsync(Guid refundId)
    {
        using var scope = _fixture.Services.CreateScope();

        try
        {
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProcessRefundCommand(refundId));
        }
        catch (Exception exception) when (exception is NeoPrint.Domain.Common.ConcurrencyException or MongoDB.Driver.MongoException)
        {
            // The racing twin lost at the row version.
        }
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
        return (await scope.ServiceProvider.GetRequiredService<NeoPrint.Application.PrintJobs.IPrintJobRepository>().FindByIdAsync(jobId))!;
    }

    private static async Task<HttpResponseMessage> AdminRefundAsync(HttpClient admin, Guid paymentId, decimal? amount, string? destination = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/payments/{paymentId}/refund")
        {
            Content = JsonContent.Create(new { amount, destination, reason = "Müştəri müraciəti: test", cancelJob = false }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> RetryAsync(HttpClient admin, Guid refundId, string destination)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/refunds/{refundId}/retry")
        {
            Content = JsonContent.Create(new { destination, note = "Bank qaytarmır, voucher verildi" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> RedeemAsync(TestUser user, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/vouchers/redeem") { Content = JsonContent.Create(new { code }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await user.Client.SendAsync(request);
    }
}
