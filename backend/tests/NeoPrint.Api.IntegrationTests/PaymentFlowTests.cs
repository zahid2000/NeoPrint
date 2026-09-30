using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Idempotency;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Payments;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Paying for a flash job at the kiosk with a bank QR (plan sections 9.1,
/// 2.5; docs/PAYMENT_FLOWS.md, docs/IDEMPOTENCY.md; docs/TASKS.md T-101).
/// Every callback goes over HTTP from the mock bank, signed, as a real bank's would.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentFlowTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PaymentFlowTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_bank_qr_payment_queues_the_job_and_tells_the_kiosk()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-success");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);

        var job = await PlaceAsync(kiosk.Client);
        job.GetProperty("status").GetString().Should().Be("PendingPayment");

        var intent = await IntentAsync(kiosk.Client, JobId(job));
        intent.GetProperty("qrContent").GetString().Should().Be($"MOCKBANK:{Id(intent)}");
        intent.GetProperty("secondsLeft").GetInt32().Should().BeInRange(1, 5 * 60);
        intent.GetProperty("amount").GetDecimal().Should().Be(job.GetProperty("price").GetProperty("total").GetDecimal());

        (await SimulateAsync(Id(intent))).StatusCode.Should().Be(HttpStatusCode.OK);

        var confirmed = await hub.WaitForAsync("PaymentConfirmed", TimeSpan.FromSeconds(10));
        confirmed.GetProperty("paymentId").GetGuid().Should().Be(Id(intent));

        var payment = await GetPaymentAsync(kiosk.Client, Id(intent));
        payment.GetProperty("status").GetString().Should().Be("Succeeded");
        payment.TryGetProperty("qrContent", out _).Should().BeFalse("a paid QR is not shown again (null is left out of the JSON)");

        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Queued);
    }

    [DockerFact]
    public async Task The_same_callback_twice_at_the_same_moment_pays_the_job_once()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-duplicate");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, JobId(job));

        (await SimulateAsync(Id(intent), "duplicate=true")).StatusCode.Should().Be(HttpStatusCode.OK);

        // And the bank resends it once more, later.
        (await SimulateAsync(Id(intent))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var payment = await context.Payments.Find(p => p.Id == Id(intent)).SingleAsync();

        payment.Status.Should().Be(PaymentStatus.Succeeded);
        payment.Timeline.Count(e => e.Event == "succeeded").Should().Be(1);

        var paidTransitions = await context.StateTransitions
            .Find(t => t.EntityId == JobId(job) && t.ToStatus == nameof(PrintJobStatus.Paid))
            .CountDocumentsAsync();
        paidTransitions.Should().Be(1, "one payment, one move to Paid, whatever the bank resends");

        var events = await context.ProviderEvents.Find(e => e.Provider == MockBankProvider.ProviderName && e.ProviderEventId.Contains(payment.ProviderRef!)).CountDocumentsAsync();
        events.Should().Be(1, "the provider_events index lets the same event in once");
    }

    [DockerFact]
    public async Task Asking_for_a_qr_again_returns_the_same_intent_and_never_a_second_payable_one()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-again");
        var job = await PlaceAsync(kiosk.Client);
        var key = Guid.NewGuid().ToString("N");

        var first = await IntentAsync(kiosk.Client, JobId(job), key: key);
        var replay = await IntentAsync(kiosk.Client, JobId(job), key: key);
        var asked = await IntentAsync(kiosk.Client, JobId(job));

        Id(replay).Should().Be(Id(first), "the same Idempotency-Key replays the first answer");
        Id(asked).Should().Be(Id(first), "a new key for the same job and method still finds the open intent");

        using var scope = _fixture.Services.CreateScope();
        var payments = await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().ListForJobAsync(JobId(job));
        payments.Should().ContainSingle();
    }

    [DockerFact]
    public async Task A_payment_made_before_switching_method_is_kept_not_charged_again()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-switch");
        var job = await PlaceAsync(kiosk.Client);
        var bankQr = await IntentAsync(kiosk.Client, JobId(job));

        // Paid in the bank app; the bank's callback is lost on the way.
        (await SimulateAsync(Id(bankQr), "dropCallback=true")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The customer taps "pay with the NeoPrint app" instead: the kiosk asks
        // the bank to close the first QR, and the bank says it was paid.
        var answer = await IntentAsync(kiosk.Client, JobId(job), method: "MobileApp");

        Id(answer).Should().Be(Id(bankQr));
        answer.GetProperty("status").GetString().Should().Be("Succeeded");
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Queued);

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().ListForJobAsync(JobId(job))).Should().ContainSingle();
    }

    [DockerFact]
    public async Task A_lost_callback_is_found_by_reconciliation_with_the_bank()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-lost");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, JobId(job));

        (await SimulateAsync(Id(intent), "dropCallback=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetPaymentAsync(kiosk.Client, Id(intent))).GetProperty("status").GetString().Should().Be("Pending");

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ReconcilePaymentCommand(Id(intent)));
        }

        (await GetPaymentAsync(kiosk.Client, Id(intent))).GetProperty("status").GetString().Should().Be("Succeeded");
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Queued);
    }

    [DockerFact]
    public async Task An_intent_that_ran_out_expires_with_its_job_and_its_qr_can_no_longer_be_paid()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-expire");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, JobId(job));

        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            await context.Payments.UpdateOneAsync(
                p => p.Id == Id(intent), Builders<Payment>.Update.Set(p => p.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));

            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ExpirePaymentCommand(Id(intent)));
        }

        (await GetPaymentAsync(kiosk.Client, Id(intent))).GetProperty("status").GetString().Should().Be("Expired");
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Expired);

        // The customer scans the stale QR: their bank refuses it, nothing is taken.
        (await SimulateAsync(Id(intent))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Expired);
    }

    [DockerFact]
    public async Task Money_the_bank_took_for_a_job_that_is_gone_is_sent_back_not_kept()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-orphan");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, JobId(job));

        using (var scope = _fixture.Services.CreateScope())
        {
            // The job was given up while the bank still held the QR open.
            var jobs = scope.ServiceProvider.GetRequiredService<MongoContext>().Database.GetCollection<BsonDocument>("print_jobs");
            await jobs.UpdateOneAsync(
                new BsonDocument("_id", new BsonBinaryData(JobId(job), GuidRepresentation.Standard)),
                new BsonDocument("$set", new BsonDocument("Status", nameof(PrintJobStatus.Expired))));
        }

        (await SimulateAsync(Id(intent))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = _fixture.Services.CreateScope();
        var payment = await check.ServiceProvider.GetRequiredService<IPaymentRepository>().FindByIdAsync(Id(intent));

        payment!.Status.Should().Be(PaymentStatus.Succeeded, "the money was taken and the books say so");
        payment.Timeline.Should().Contain(e => e.Event == "refundRequested" && e.Details == OrphanRefundReasons.LatePayment);
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.Expired, "an expired job is not revived by a late payment");
    }

    [DockerFact]
    public async Task A_callback_without_the_banks_signature_is_refused_and_changes_nothing()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-forged");
        var job = await PlaceAsync(kiosk.Client);
        var intent = await IntentAsync(kiosk.Client, JobId(job));

        using var scope = _fixture.Services.CreateScope();
        var providerRef = (await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().FindByIdAsync(Id(intent)))!.ProviderRef!;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId = "forged-1",
            type = "payment.succeeded",
            transactionId = providerRef,
            amountQapik = 20,
            occurredAt = DateTime.UtcNow,
        });

        using var anonymous = _fixture.CreateApiClient();

        foreach (var signature in new[] { "0000", MockBankProvider.Sign("not-the-secret", body) })
        {
            using var content = new ByteArrayContent(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/callback/mockbank") { Content = content };
            request.Headers.Add(MockBankProvider.SignatureHeaderName, signature);

            (await anonymous.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await anonymous.PostAsync("/api/payments/callback/nosuchbank", new ByteArrayContent(body))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await GetPaymentAsync(kiosk.Client, Id(intent))).GetProperty("status").GetString().Should().Be("Pending");
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.PendingPayment);
    }

    [DockerFact]
    public async Task A_bank_that_does_not_answer_starts_no_payment_and_says_what_to_do()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-down");
        var job = await PlaceAsync(kiosk.Client);
        using var anonymous = _fixture.CreateApiClient();

        (await anonymous.PostAsync("/api/dev/mock-bank/create-fails?fails=true", null)).EnsureSuccessStatusCode();

        try
        {
            var response = await SendIntentAsync(kiosk.Client, JobId(job), "BankQr", Guid.NewGuid().ToString("N"));
            var raw = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, raw);
            raw.Should().Contain("payments.providerUnavailable");
        }
        finally
        {
            (await anonymous.PostAsync("/api/dev/mock-bank/create-fails?fails=false", null)).EnsureSuccessStatusCode();
        }

        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IPaymentRepository>().ListForJobAsync(JobId(job))).Should().BeEmpty();
        (await JobStatusAsync(JobId(job))).Should().Be(PrintJobStatus.PendingPayment, "the customer can still pay another way");
    }

    [DockerFact]
    public async Task The_timeout_set_in_the_panel_is_the_countdown_and_the_expiry()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync("pay-timeout");
        using var scope = _fixture.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await settings.UpdateAsync(new Dictionary<string, string> { ["Payment.TimeoutMinutes"] = "2" }, updatedBy: null, CancellationToken.None);

        try
        {
            var job = await PlaceAsync(kiosk.Client);
            var before = DateTime.UtcNow;
            var intent = await IntentAsync(kiosk.Client, JobId(job));

            intent.GetProperty("secondsLeft").GetInt32().Should().BeInRange(100, 120);
            intent.GetProperty("expiresAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(before.AddMinutes(2), TimeSpan.FromSeconds(15));
        }
        finally
        {
            await settings.UpdateAsync(new Dictionary<string, string> { ["Payment.TimeoutMinutes"] = "5" }, updatedBy: null, CancellationToken.None);
        }
    }

    /// <summary>An activated kiosk with paper in the A4 tray: an order it cannot cover is refused (plan section 2.15).</summary>
    private async Task<TestKiosk> ReadyKioskAsync(string name)
    {
        var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"{name}-{Guid.NewGuid():N}"[..30]);

        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId),
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Update.Set("PaperStock.A4.Current", 500));

        return kiosk;
    }

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    private static Guid JobId(JsonElement job) => job.GetProperty("id").GetGuid();

    private static async Task<JsonElement> PlaceAsync(HttpClient kiosk)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = 2,
                options = new { copies = 1, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = false,
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await kiosk.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendIntentAsync(HttpClient kiosk, Guid jobId, string method, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/intents")
        {
            Content = JsonContent.Create(new { jobId, method }),
        };

        request.Headers.Add("Idempotency-Key", key);

        return await kiosk.SendAsync(request);
    }

    private static async Task<JsonElement> IntentAsync(HttpClient kiosk, Guid jobId, string method = "BankQr", string? key = null)
    {
        var response = await SendIntentAsync(kiosk, jobId, method, key ?? Guid.NewGuid().ToString("N"));
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SimulateAsync(Guid paymentId, string query = "")
    {
        using var anonymous = _fixture.CreateApiClient();
        return await anonymous.PostAsync($"/api/dev/payments/{paymentId}/simulate-success?{query}", null);
    }

    private static async Task<JsonElement> GetPaymentAsync(HttpClient kiosk, Guid paymentId)
        => await kiosk.GetFromJsonAsync<JsonElement>($"/api/payments/{paymentId}");

    private async Task<PrintJobStatus> JobStatusAsync(Guid jobId)
    {
        using var scope = _fixture.Services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<NeoPrint.Application.PrintJobs.IPrintJobRepository>().FindByIdAsync(jobId);

        return job!.Status;
    }
}
