using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// One app user and another's records (T-312): every endpoint of the app that
/// takes a record by id, tried by user A with user B's ids - read, change,
/// cancel, delete - answers 404, never 403 (which would say the record is
/// there) and never success; and every list of A holds nothing of B.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UserIsolationTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public UserIsolationTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task One_user_can_neither_read_nor_change_nor_cancel_nor_delete_anything_of_another_and_is_not_told_it_exists()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "idor-kiosk");
        using var a = await TestUsers.ConfirmedAsync(_fixture);
        using var b = await TestUsers.ConfirmedAsync(_fixture);
        using var c = await TestUsers.ConfirmedAsync(_fixture);

        var theirs = await RecordsOfAsync(b, c, kiosk);

        string? IdFor(string template) => template switch
        {
            _ when template.StartsWith("api/files/", StringComparison.Ordinal) => theirs["file"],
            _ when template.StartsWith("api/prepared-prints/paid/", StringComparison.Ordinal) => theirs["job"],
            _ when template.StartsWith("api/prepared-prints/", StringComparison.Ordinal) => theirs["prepared"],
            _ when template.StartsWith("api/schedules/", StringComparison.Ordinal) => theirs["schedule"],
            _ when template.StartsWith("api/sessions/", StringComparison.Ordinal) => theirs["session"],
            _ when template.StartsWith("api/print-jobs/my/", StringComparison.Ordinal) => theirs["job"],
            _ when template.StartsWith("api/receipts/", StringComparison.Ordinal) => theirs["receipt"],
            _ when template.StartsWith("api/notifications/", StringComparison.Ordinal) => theirs["notification"],
            _ when template.StartsWith("api/shares/", StringComparison.Ordinal) => theirs["share"],
            _ => null,
        };

        var tried = new List<string>();
        var wrong = new List<string>();

        foreach (var endpoint in _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var template = endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty;
            var forUsers = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == AuthorizationPolicies.User);
            if (!forUsers || !template.Contains('{', StringComparison.Ordinal) || IdFor(template) is not { } id)
            {
                continue;
            }

            // The record's id; a page, a version or an asset key next to it.
            var path = "/" + Regex.Replace(template, @"\{(\w+)(:[^}]+)?\}", match => match.Groups[1].Value switch
            {
                "page" or "number" => "1",
                "key" => "asset",
                _ => id,
            });

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
                if (method is "POST" or "PUT")
                {
                    request.Content = JsonContent.Create(Body(template));
                }

                using var response = await a.Client.SendAsync(request);
                tried.Add($"{method} {template}: {(int)response.StatusCode}");

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    wrong.Add($"{method} {template}: {(int)response.StatusCode} {(await response.Content.ReadAsStringAsync())[..Math.Min(160, (int)(response.Content.Headers.ContentLength ?? 160))]}");
                }
            }
        }

        // A claim about someone else's order: not found either.
        using (var claim = new HttpRequestMessage(HttpMethod.Post, "/api/me/claims") { Content = JsonContent.Create(new { jobId = theirs["job"], text = "IDOR" }) })
        {
            claim.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
            var claimed = await a.Client.SendAsync(claim);
            tried.Add($"POST api/me/claims (their order): {(int)claimed.StatusCode}");
            if (claimed.StatusCode != HttpStatusCode.NotFound) { wrong.Add(tried[^1]); }
        }

        // Signing a phone out of someone else's push: their phone keeps getting pushes.
        using (var unregister = new HttpRequestMessage(HttpMethod.Delete, "/api/users/me/fcm-token") { Content = JsonContent.Create(new { token = theirs["push"] }) })
        {
            await a.Client.SendAsync(unregister);
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.Find(user => user.Id == b.Id).SingleAsync();
            stored.FcmTokens.Select(token => token.Token).Should().Contain(theirs["push"], "one user cannot take another's phone off its pushes");
            tried.Add("DELETE api/users/me/fcm-token (their token): their phone keeps its pushes");
        }

        // The lists: only A's own.
        foreach (var list in new[]
                 {
                     "api/files", "api/prepared-prints", "api/print-jobs/my", "api/receipts", "api/schedules", "api/notifications",
                     "api/shares/received", "api/shares/sent", "api/me/claims", "api/wallet", "api/wallet/transactions", "api/sessions/current",
                     "api/referrals/me",
                 })
        {
            using var response = await a.Client.GetAsync(list);
            var body = await response.Content.ReadAsStringAsync();
            var named = theirs.Where(pair => pair.Key != "push" && body.Contains(pair.Value, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToList();
            tried.Add($"GET {list}: {(int)response.StatusCode}");

            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent) || named.Count > 0 || body.Contains(b.Email, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"GET {list}: {(int)response.StatusCode}, names B's {string.Join(", ", named)}");
            }
        }

        Console.WriteLine(string.Join(Environment.NewLine, tried));
        tried.Should().HaveCountGreaterThan(30, "every endpoint of the app that takes a record is tried");
        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));

        // B's records are as they were.
        (await b.Client.GetAsync($"/api/files/{theirs["file"]}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/prepared-prints")).ToString().Should().Contain(theirs["prepared"]);
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/schedules")).ToString().Should().Contain(theirs["schedule"]);
    }

    /// <summary>A body that would pass for the endpoint, so a refusal is the record's and not the body's.</summary>
    private static object Body(string template) => template switch
    {
        _ when template.EndsWith("/edit", StringComparison.Ordinal) => new { pages = new[] { new { page = 1 } } },
        _ when template.EndsWith("/layout", StringComparison.Ordinal) => new
        {
            layout = new
            {
                version = 1, paper = "A4", orientation = "Portrait", marginMm = 10,
                pages = new[] { new { elements = new[] { new { image = $"file:{Guid.NewGuid()}:1", xMm = 20.0, yMm = 20.0, widthMm = 85.6, heightMm = 53.98, rotationDeg = 0.0 } } } },
            },
        },
        _ when template.EndsWith("/auto-delete", StringComparison.Ordinal) => new { rule = "Never" },
        _ when template.EndsWith("/time", StringComparison.Ordinal) => new { scheduledAt = DateTime.UtcNow.AddHours(5) },
        _ when template.EndsWith("/email", StringComparison.Ordinal) => new { email = "idor@neoprint.test" },
        _ when template.EndsWith("/print-jobs", StringComparison.Ordinal) => new { fileId = Guid.NewGuid(), options = BlackAndWhite },
        _ => new { },
    };

    /// <summary>B's file, preparation, schedule, kiosk session, paid order with its receipt, a notification, a push token and a share to C.</summary>
    private async Task<Dictionary<string, string>> RecordsOfAsync(TestUser b, TestUser c, TestKiosk kiosk)
    {
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, b.Client, pages: 1);

        async Task<JsonElement> PostAsync(string url, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
            var response = await b.Client.SendAsync(request);
            response.IsSuccessStatusCode.Should().BeTrue($"{url}: {await response.Content.ReadAsStringAsync()}");
            return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var prepared = await PostAsync("/api/prepared-prints", new { fileId, options = new { color = false } });
        var schedule = await PostAsync("/api/schedules", new { kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(2), expectedAmount = 0.20m });
        var session = await PostAsync("/api/sessions", new { kiosk = kiosk.KioskCode });
        var share = await PostAsync("/api/shares", new { fileId, toPublicId = await PublicIdAsync(c) });

        // C shares a file with B: a notification of B's own.
        var cFile = await SessionTests.UploadReadyPdfAsync(_fixture, c.Client, pages: 1);
        using (var toB = new HttpRequestMessage(HttpMethod.Post, "/api/shares") { Content = JsonContent.Create(new { fileId = cFile, toPublicId = await PublicIdAsync(b) }) })
        {
            toB.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
            (await c.Client.SendAsync(toB)).IsSuccessStatusCode.Should().BeTrue();
        }

        var push = $"idor-phone-{Guid.NewGuid():N}";
        (await b.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token = push, language = "az", platform = "android" })).IsSuccessStatusCode.Should().BeTrue();

        // A paid order at the kiosk, paid from B's wallet: a job, a payment, a receipt.
        await PostAsync("/api/wallet/top-up", new { amount = 5m });
        var job = (await PlaceAsync(kiosk.Client)).GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, job, "MobileApp");
        var paymentId = intent.GetProperty("id").GetGuid();
        await PostAsync($"/api/payments/{paymentId}/pay-with-app", new { source = "Wallet", useBonus = false });

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var receipt = await EventuallyAsync(() => context.Receipts.Find(r => r.PaymentId == paymentId).FirstOrDefaultAsync(), r => r is not null, "B's receipt");
        var notification = await EventuallyAsync(() => context.Notifications.Find(n => n.UserId == b.Id).FirstOrDefaultAsync(), n => n is not null, "a notification of B");

        return new Dictionary<string, string>
        {
            ["file"] = fileId.ToString(),
            ["prepared"] = prepared.GetProperty("id").ToString(),
            ["schedule"] = schedule.GetProperty("scheduleId").ToString(),
            ["session"] = session.GetProperty("id").ToString(),
            ["share"] = share.GetProperty("id").ToString(),
            ["job"] = job.ToString(),
            ["payment"] = paymentId.ToString(),
            ["receipt"] = receipt!.Id.ToString(),
            ["notification"] = notification!.Id.ToString(),
            ["push"] = push,
        };
    }

    private async Task<string> PublicIdAsync(TestUser user)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.Find(u => u.Id == user.Id).SingleAsync()).PublicId;
    }
}
