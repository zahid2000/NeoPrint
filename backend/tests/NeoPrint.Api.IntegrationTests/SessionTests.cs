using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Sessions;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using StackExchange.Redis;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Printing in a session (plan section 9.3, step 5 / M2): the session in Redis
/// with its time to live, its kiosk told when it opens and ends, an order for a
/// file in the account paid with the app, and the kiosk fetching that PDF.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SessionTests
{
    private static readonly TimeSpan HubWait = TimeSpan.FromSeconds(10);

    private readonly NeoPrintApiFixture _fixture;

    public SessionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_session_opens_by_code_tells_its_kiosk_and_opening_again_extends_it()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "session-open");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        var first = await OpenAsync(user.Client, kiosk.KioskCode.ToLowerInvariant());
        first.GetProperty("kioskId").GetGuid().Should().Be(kiosk.KioskId);
        first.GetProperty("ttlMinutes").GetInt32().Should().Be(10);

        var opened = await hub.WaitForAsync("SessionOpened", HubWait);
        opened.GetProperty("sessionId").GetGuid().Should().Be(Id(first));
        opened.ToString().Should().NotContain(user.Email, "the kiosk screen is public");

        await Task.Delay(50);
        var again = await OpenAsync(user.Client, kiosk.KioskId.ToString());
        Id(again).Should().Be(Id(first), "the same kiosk again is the same session");
        again.GetProperty("expiresAt").GetDateTime().Should().BeAfter(first.GetProperty("expiresAt").GetDateTime());

        (await user.Client.GetFromJsonAsync<JsonElement>("/api/sessions/current")).GetProperty("id").GetGuid().Should().Be(Id(first));

        // Somebody else's session reads as none.
        using var other = await TestUsers.ConfirmedAsync(_fixture);
        (await other.Client.GetAsync($"/api/sessions/{Id(first)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Client.GetAsync("/api/sessions/current")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Another_kiosk_replaces_the_session_and_closing_is_told_once()
    {
        if (Skip)
        {
            return;
        }

        using var first = await ReadyKioskAsync(_fixture, "session-a");
        using var second = await ReadyKioskAsync(_fixture, "session-b");
        await using var hubA = await RawHubConnection.ConnectAsync(_fixture, "kiosk", first.AccessToken);
        await using var hubB = await RawHubConnection.ConnectAsync(_fixture, "kiosk", second.AccessToken);
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        var a = await OpenAsync(user.Client, first.KioskCode);
        var b = await OpenAsync(user.Client, second.KioskCode);
        Id(b).Should().NotBe(Id(a));

        var replaced = await hubA.WaitForAsync("SessionClosed", HubWait);
        replaced.GetProperty("sessionId").GetGuid().Should().Be(Id(a));
        replaced.GetProperty("reason").GetString().Should().Be("Replaced");
        (await user.Client.GetAsync($"/api/sessions/{Id(a)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await user.Client.DeleteAsync($"/api/sessions/{Id(b)}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await hubB.WaitForAsync("SessionClosed", HubWait)).GetProperty("reason").GetString().Should().Be("Closed");

        // Already over: still no error, and nothing to tell twice.
        (await user.Client.DeleteAsync($"/api/sessions/{Id(b)}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.GetAsync("/api/sessions/current")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task A_kiosk_not_taking_orders_opens_no_session_and_a_hidden_one_is_not_found()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "session-paused");
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        await SetStatusAsync(kiosk.KioskId, KioskStatus.Paused);
        var paused = await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = kiosk.KioskCode });
        paused.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await paused.Content.ReadAsStringAsync()).Should().Contain("sessions.kioskNotTakingOrders");

        await SetStatusAsync(kiosk.KioskId, KioskStatus.Disabled);
        (await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = kiosk.KioskCode })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var empty = await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = " " });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await kiosk.Client.PostAsJsonAsync("/api/sessions", new { kiosk = kiosk.KioskCode })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task An_expired_session_is_told_to_its_kiosk_and_app_once()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "session-expiry");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        await using var app = await RawHubConnection.ConnectAsync(_fixture, "user", user.Client.DefaultRequestHeaders.Authorization!.Parameter!);

        var session = await OpenAsync(user.Client, kiosk.KioskCode);
        await hub.WaitForAsync("SessionOpened", HubWait);

        // Its time ran out (the ten minutes, without waiting them).
        await _fixture.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
            .SortedSetAddAsync("sessions:expiring", Id(session).ToString("N"), DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());

        (await SendAsync(new ExpireKioskSessionsCommand())).Should().BeGreaterThanOrEqualTo(1);
        (await SendAsync(new ExpireKioskSessionsCommand())).Should().Be(0, "each expiry is announced once");

        (await hub.WaitForAsync("SessionClosed", HubWait)).GetProperty("reason").GetString().Should().Be("Expired");
        (await app.WaitForAsync("SessionClosed", HubWait)).GetProperty("sessionId").GetGuid().Should().Be(Id(session));
        (await user.Client.GetAsync($"/api/sessions/{Id(session)}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An order in it now: the session is gone.
        var order = await PlaceAsync(user.Client, Id(session), Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        order.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await order.Content.ReadAsStringAsync()).Should().Contain("notFound.session");
    }

    [PdfiumFact]
    public async Task An_order_in_a_session_is_paid_with_the_app_and_its_kiosk_fetches_the_pdf_it_was_priced_on()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "session-order");
        using var elsewhere = await ReadyKioskAsync(_fixture, "session-other");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await UploadReadyPdfAsync(_fixture, user.Client, pages: 3);
        var session = await OpenAsync(user.Client, kiosk.KioskCode);

        // A retry with the same key is the same order, not a second one.
        var key = Guid.NewGuid().ToString("N");
        var placed = await PlaceAsync(user.Client, Id(session), fileId, key);
        placed.StatusCode.Should().Be(HttpStatusCode.OK, await placed.Content.ReadAsStringAsync());
        var order = await placed.Content.ReadFromJsonAsync<JsonElement>();
        var retried = await (await PlaceAsync(user.Client, Id(session), fileId, key)).Content.ReadFromJsonAsync<JsonElement>();
        retried.GetProperty("jobId").GetGuid().Should().Be(order.GetProperty("jobId").GetGuid());

        var jobId = order.GetProperty("jobId").GetGuid();
        var paymentId = order.GetProperty("paymentId").GetGuid();
        order.GetProperty("amount").GetDecimal().Should().Be(0.30m, "3 black and white A4 sides at 0,10 ₼");
        order.GetProperty("pages").GetInt32().Should().Be(3);

        // Not paid yet: the kiosk gets nothing to print.
        (await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The file stays as it was ordered while the order is open.
        var delete = await user.Client.DeleteAsync($"/api/files/{fileId}");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await delete.Content.ReadAsStringAsync()).Should().Contain("files.inUseByOrder");

        // Paid the way the app pays a kiosk's QR.
        using (var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{paymentId}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Card", useBonus = false, expectedAmount = 0.30m }),
        })
        {
            pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            var paid = await user.Client.SendAsync(pay);
            paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
        }

        var queued = await EventuallyAsync(
            () => kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}"),
            job => job.GetProperty("status").GetString() == "Queued",
            "the paid order in the queue");
        queued.GetProperty("source").GetString().Should().Be("MobileSession");

        // The kiosk gets the very PDF the order was priced on; no other kiosk does.
        var fetched = await kiosk.Client.GetAsync($"/api/print-jobs/{jobId}/file");
        fetched.StatusCode.Should().Be(HttpStatusCode.OK);
        fetched.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await fetched.Content.ReadAsByteArrayAsync()).Should().Equal(await user.Client.GetByteArrayAsync($"/api/files/{fileId}/pdf"));
        (await elsewhere.Client.GetAsync($"/api/print-jobs/{jobId}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await user.Client.GetFromJsonAsync<JsonElement>("/api/print-jobs/my")).EnumerateArray()
            .Should().Contain(job => job.GetProperty("id").GetGuid() == jobId);

        // Printed: the file is the user's to change again.
        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Completed", sides: 3, sheets: 3);
        (await user.Client.DeleteAsync($"/api/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Only_the_session_owner_s_own_file_is_ordered()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "session-foreign");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        var session = await OpenAsync(user.Client, kiosk.KioskCode);

        // A file id that is not the user's: no file.
        var order = await PlaceAsync(user.Client, Id(session), Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        order.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await order.Content.ReadAsStringAsync()).Should().Contain("notFound.file");

        // Another user's session: none.
        var foreign = await PlaceAsync(stranger.Client, Id(session), Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).Should().Contain("notFound.session");
    }

    private static Guid Id(JsonElement session) => session.GetProperty("id").GetGuid();

    private static async Task<JsonElement> OpenAsync(HttpClient user, string kiosk)
    {
        var response = await user.PostAsJsonAsync("/api/sessions", new { kiosk });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PlaceAsync(HttpClient user, Guid sessionId, Guid fileId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                fileId,
                options = new { copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
            }),
        };

        request.Headers.Add("Idempotency-Key", key);
        return await user.SendAsync(request);
    }

    /// <summary>A PDF of <paramref name="pages"/> pages in the user's files, converted and ready to order.</summary>
    internal static async Task<Guid> UploadReadyPdfAsync(NeoPrintApiFixture fixture, HttpClient user, int pages)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(ConversionTests.OnePagePdf(pages));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", "hesabat.pdf");

        var upload = await user.PostAsync("/api/files", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            using var scope = fixture.Services.CreateScope();
            var file = await scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Files.IFileRepository>().FindByIdAsync(id);

            if (file!.Status == NeoPrint.Domain.Files.FileStatus.Ready)
            {
                return id;
            }

            file.Status.Should().NotBe(NeoPrint.Domain.Files.FileStatus.Failed);

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"File {id} is still {file.Status}.");
            }

            try
            {
                await scope.ServiceProvider.GetRequiredService<NeoPrint.Infrastructure.Conversion.DocumentConversionService>().ConvertAsync(id);
            }
            catch (NeoPrint.Domain.Common.DomainException)
            {
                await Task.Delay(200);
            }
        }
    }

    private async Task SetStatusAsync(Guid kioskId, KioskStatus status)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(k => k.Id, kioskId), Builders<Kiosk>.Update.Set(k => k.Status, status));
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }
}
