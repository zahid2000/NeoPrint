using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>What the payment and refund tests share: a kiosk with paper, an order, an intent, a paying customer.</summary>
internal static class PaymentTestKit
{
    public static async Task<TestKiosk> ReadyKioskAsync(NeoPrintApiFixture fixture, string name)
    {
        var kiosk = await TestKiosks.ActivatedAsync(fixture, $"{name}-{Guid.NewGuid():N}"[..30]);

        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Filter.Eq(k => k.Id, kiosk.KioskId),
            Builders<NeoPrint.Domain.Kiosks.Kiosk>.Update
                .Set("PaperStock.A4.Current", 500)
                // Its own fiscal till, as a real kiosk has (plan section 2.12).
                .Set(k => k.FiscalTerminalId, $"TERM-{kiosk.KioskId.ToString("N")[..8]}"));

        return kiosk;
    }

    /// <summary>A flash order of <paramref name="pages"/> black and white A4 pages (0,10 ₼ a side, 0,20 ₼ minimum).</summary>
    public static async Task<JsonElement> PlaceAsync(HttpClient kiosk, int pages = 3, string? promoCode = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = pages,
                options = new { copies = 1, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = false,
                promoCode,
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await kiosk.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    public static async Task<JsonElement> IntentAsync(HttpClient kiosk, Guid jobId, string method = "BankQr")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/intents") { Content = JsonContent.Create(new { jobId, method }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await kiosk.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    public static async Task<HttpResponseMessage> SimulateAsync(NeoPrintApiFixture fixture, Guid paymentId, string query = "")
    {
        using var anonymous = fixture.CreateApiClient();
        return await anonymous.PostAsync($"/api/dev/payments/{paymentId}/simulate-success?{query}", null);
    }

    /// <summary>A flash order paid at the kiosk with a bank QR: the job is Queued.</summary>
    public static async Task<(Guid JobId, Guid PaymentId, decimal Price)> PaidBankJobAsync(NeoPrintApiFixture fixture, TestKiosk kiosk, int pages = 3)
    {
        var job = await PlaceAsync(kiosk.Client, pages);
        var jobId = job.GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, jobId);
        var paymentId = intent.GetProperty("id").GetGuid();

        (await SimulateAsync(fixture, paymentId)).StatusCode.Should().Be(HttpStatusCode.OK);

        return (jobId, paymentId, intent.GetProperty("amount").GetDecimal());
    }

    public static async Task ReportAsync(HttpClient kiosk, Guid jobId, string status, int sides = 0, int sheets = 0, string? errorCode = null, object? attempt = null)
    {
        var response = await kiosk.PostAsJsonAsync($"/api/print-jobs/{jobId}/status", new { status, printedSides = sides, printedSheets = sheets, errorCode, attempt });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Waits for something a consumer does asynchronously.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;

        do
        {
            value = await read();

            if (done(value))
            {
                return value;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Waited {seconds} s for {what}.");
    }
}
