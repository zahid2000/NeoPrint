using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The customer app's side of step 4 (docs/TASKS.md T-109): <c>/hubs/user</c>
/// tells only the owner that their payment and job moved on, and
/// <c>GET print-jobs/my</c> lists the jobs that belong to the account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UserHubTests
{
    private static readonly TimeSpan HubWait = TimeSpan.FromSeconds(20);

    private readonly NeoPrintApiFixture _fixture;

    public UserHubTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_payer_hears_the_payment_and_the_job_and_finds_the_job_in_their_list_but_nobody_else_does()
    {
        if (Skip)
        {
            return;
        }

        using var payer = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        (await TopUpAsync(payer, 5m)).EnsureSuccessStatusCode();

        var heard = new ConcurrentQueue<(string Method, JsonElement Payload)>();
        var strangerHeard = new ConcurrentQueue<(string Method, JsonElement Payload)>();
        await using var hub = await ConnectAsync(payer, heard);
        await using var strangerHub = await ConnectAsync(stranger, strangerHeard);

        using var kiosk = await ReadyKioskAsync(_fixture, "user-hub");
        var job = await PlaceAsync(kiosk.Client);
        var jobId = job.GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, jobId, "MobileApp");
        var paymentId = intent.GetProperty("id").GetGuid();

        using (var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app") { Content = JsonContent.Create(new { source = "Wallet", useBonus = false }) })
        {
            pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            var paid = await payer.Client.SendAsync(pay);
            paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
        }

        await UntilAsync(
            () => heard.Any(m => m.Method == "PaymentStatusChanged" && m.Payload.GetProperty("paymentId").GetGuid() == paymentId
                                 && m.Payload.GetProperty("status").GetString() == "Succeeded"),
            "the payment's message");
        await UntilAsync(
            () => heard.Any(m => m.Method == "JobStatusChanged" && m.Payload.GetProperty("jobId").GetGuid() == jobId
                                 && m.Payload.GetProperty("status").GetString() == "Queued"),
            "the job's message");

        var mine = await payer.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my");
        var listed = mine.EnumerateArray().Should().ContainSingle(j => j.GetProperty("id").GetGuid() == jobId).Subject;
        listed.GetProperty("status").GetString().Should().Be("Queued");
        listed.GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);
        listed.GetProperty("paymentId").GetGuid().Should().Be(paymentId);
        listed.GetProperty("jobCode").GetString().Should().Be(job.GetProperty("jobCode").GetString());

        (await stranger.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my")).EnumerateArray().Should().BeEmpty();
        strangerHeard.Should().BeEmpty("one customer never hears about another's jobs or money");
    }

    [DockerFact]
    public async Task Only_a_user_token_opens_the_user_hub_and_the_job_list()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "user-hub-kiosk");
        using var anonymous = _fixture.CreateApiClient();

        (await anonymous.PostAsync("/hubs/user/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await kiosk.Client.PostAsync("/hubs/user/negotiate?negotiateVersion=1", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await kiosk.Client.GetAsync("/api/print-jobs/my")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await anonymous.GetAsync("/api/print-jobs/my")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HubConnection> ConnectAsync(TestUser user, ConcurrentQueue<(string, JsonElement)> heard)
    {
        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter!;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_fixture.Server.BaseAddress, "/hubs/user"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => _fixture.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        foreach (var method in new[] { "JobStatusChanged", "PaymentStatusChanged", "RefundStatusChanged" })
        {
            connection.On<JsonElement>(method, payload => heard.Enqueue((method, payload)));
        }

        await connection.StartAsync();
        return connection;
    }

    private static async Task<HttpResponseMessage> TopUpAsync(TestUser user, decimal amount)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await user.Client.SendAsync(request);
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow + HubWait;

        while (!done())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Waited {HubWait.TotalSeconds:0} s for {what}.");
            }

            await Task.Delay(100);
        }
    }
}
