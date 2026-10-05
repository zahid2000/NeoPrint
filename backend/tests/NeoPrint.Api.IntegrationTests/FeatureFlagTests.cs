using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Feature flags close new usage only (plan section 2.16, <c>docs/DECISIONS.md</c>
/// T-148): a switched-off feature refuses what would be created or paid now
/// with <c>404 features.unavailable</c> and the flag key, while what the user
/// already paid for is still listed, moved, released and printed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FeatureFlagTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public FeatureFlagTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task A_kiosk_switched_off_takes_nothing_new_but_what_was_paid_there_carries_on()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        using var kiosk = await ReadyKioskAsync(_fixture, "flags-kiosk");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);

        // Made and paid while everything was on.
        var schedule = await PostAsync(user.Client, "/api/schedules", new { kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(2), expectedAmount = 0.30m });
        schedule.StatusCode.Should().Be(HttpStatusCode.OK, await schedule.Content.ReadAsStringAsync());
        var scheduled = await schedule.Content.ReadFromJsonAsync<JsonElement>();
        await PayAsync(user.Client, scheduled.GetProperty("paymentId").GetGuid());
        var heldJobId = await PaidPreparationAsync(user.Client, kiosk.KioskCode, fileId);
        var prepared = await PostAsync(user.Client, "/api/prepared-prints", new { fileId, options = new { color = false } });
        prepared.StatusCode.Should().Be(HttpStatusCode.OK, await prepared.Content.ReadAsStringAsync());
        var preparedId = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var kioskOrder = (await PlaceAsync(kiosk.Client)).GetProperty("id").GetGuid();
        var appQr = await IntentAsync(kiosk.Client, kioskOrder, "MobileApp");

        string[] keys = ["scheduled_print", "user_qr_print", "mobile_session_print", "mobile_payment_at_kiosk"];
        var before = await FlagsAsync(admin);

        try
        {
            foreach (var key in keys)
            {
                var flag = before[key];
                var overrides = flag.GetProperty("kioskOverrides").Deserialize<Dictionary<string, bool>>()!;
                overrides[kiosk.KioskId.ToString()] = false;
                await PutAsync(admin, flag, flag.GetProperty("defaultEnabled").GetBoolean(), overrides);
            }

            // New usage at this kiosk: refused, naming the flag.
            await RefusedAsync(
                await PostAsync(user.Client, "/api/schedules", new { kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(3), expectedAmount = 0.30m }),
                "scheduled_print");
            await RefusedAsync(
                await PostAsync(user.Client, "/api/prepared-prints/paid", new { kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, expectedAmount = 0.30m }),
                "user_qr_print");
            await RefusedAsync(await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = kiosk.KioskCode }), "mobile_session_print");
            await RefusedAsync(await RawIntentAsync(kiosk.Client, (await PlaceAsync(kiosk.Client)).GetProperty("id").GetGuid(), "MobileApp"), "mobile_payment_at_kiosk");

            // The kiosk's "pay with the app" QR shown before the switch: not paid yet, so not payable now.
            var appPaymentId = appQr.GetProperty("id").GetGuid();
            await RefusedAsync(await user.Client.GetAsync($"/api/payments/{appPaymentId}/app"), "mobile_payment_at_kiosk");
            await RefusedAsync(
                await PostAsync(user.Client, $"/api/payments/{appPaymentId}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m }),
                "mobile_payment_at_kiosk");

            // The bank QR is not this flag.
            (await RawIntentAsync(kiosk.Client, kioskOrder, "BankQr")).StatusCode.Should().Be(HttpStatusCode.OK);

            // The personal QR still opens what was paid; the unpaid preparation is not offered here.
            var code = (await user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/qr")).GetProperty("code").GetString();
            var scan = await kiosk.Client.PostAsJsonAsync("/api/print-jobs/pending-for-user", new { code });
            scan.StatusCode.Should().Be(HttpStatusCode.OK, await scan.Content.ReadAsStringAsync());
            var files = await scan.Content.ReadFromJsonAsync<JsonElement>();
            var grant = files.GetProperty("grant").GetGuid();
            files.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("kind").GetString()).Should().Equal("Paid");
            await RefusedAsync(await OrderPreparedAsync(kiosk.Client, grant, preparedId), "user_qr_print");
            var released = await ReleaseAsync(kiosk.Client, grant, heldJobId);
            released.StatusCode.Should().Be(HttpStatusCode.OK, await released.Content.ReadAsStringAsync());

            // The paid schedule: listed, moved and sent as before.
            var scheduleId = scheduled.GetProperty("scheduleId").GetGuid();
            var schedules = await user.Client.GetFromJsonAsync<JsonElement>("/api/schedules");
            schedules.GetProperty("items").EnumerateArray().Should().Contain(s => s.GetProperty("id").GetGuid() == scheduleId);
            (await user.Client.PutAsJsonAsync($"/api/schedules/{scheduleId}/time", new { scheduledAt = DateTime.UtcNow.AddHours(4) }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            var sent = await PostAsync(user.Client, $"/api/schedules/{scheduleId}/print-now", new { });
            sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());

            // Another kiosk is not this pilot switch.
            using var other = await ReadyKioskAsync(_fixture, "flags-other");
            (await user.Client.PostAsJsonAsync("/api/sessions", new { kiosk = other.KioskCode })).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await RestoreAsync(admin, before, keys);
        }
    }

    [PdfiumFact]
    public async Task Switched_off_for_everyone_the_app_is_told_and_the_endpoints_are_closed()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 1);

        string[] keys = ["data_export", "user_qr_print", "file_sharing"];
        var before = await FlagsAsync(admin);

        try
        {
            foreach (var key in keys)
            {
                var flag = before[key];
                await PutAsync(admin, flag, false, flag.GetProperty("kioskOverrides").Deserialize<Dictionary<string, bool>>()!);
            }

            var features = await user.Client.GetFromJsonAsync<JsonElement>("/api/features");
            features.GetProperty("data_export").GetBoolean().Should().BeFalse();
            features.GetProperty("user_qr_print").GetBoolean().Should().BeFalse();
            features.GetProperty("file_sharing").GetBoolean().Should().BeFalse();
            features.GetProperty("scheduled_print").GetBoolean().Should().BeTrue();

            await RefusedAsync(await PostAsync(user.Client, "/api/users/me/export", new { }), "data_export");
            await RefusedAsync(await PostAsync(user.Client, "/api/prepared-prints", new { fileId, options = new { color = false } }), "user_qr_print");
            await RefusedAsync(await PostAsync(user.Client, "/api/shares", new { fileId, toPublicId = "NP-000000" }), "file_sharing");

            // Reading stays open: the list and the QR that collects a paid order.
            (await user.Client.GetAsync("/api/prepared-prints")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await user.Client.GetAsync("/api/users/me/qr")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await user.Client.GetAsync("/api/shares/received")).StatusCode.Should().Be(HttpStatusCode.OK, "what was already sent can still be saved");
            (await user.Client.GetAsync("/api/users/me/export")).StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await RestoreAsync(admin, before, keys);
        }

        (await PostAsync(user.Client, "/api/users/me/export", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task A_switch_reaches_every_connected_kiosk_and_its_configuration_says_so()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await SuperAdminAsync();
        using var kiosk = await ReadyKioskAsync(_fixture, "flags-hub");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);
        var before = await FlagsAsync(admin);
        var flag = before["mobile_payment_at_kiosk"];

        try
        {
            var overrides = flag.GetProperty("kioskOverrides").Deserialize<Dictionary<string, bool>>()!;
            overrides[kiosk.KioskId.ToString()] = false;
            await PutAsync(admin, flag, flag.GetProperty("defaultEnabled").GetBoolean(), overrides);

            // T-160: told at once, not at its next poll; the configuration then says off.
            await hub.WaitForAsync("KioskConfigChanged", TimeSpan.FromSeconds(10));
            var config = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");
            config.GetProperty("features").GetProperty("mobile_payment_at_kiosk").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await RestoreAsync(admin, before, ["mobile_payment_at_kiosk"]);
        }
    }

    private static async Task RefusedAsync(HttpResponseMessage response, string feature)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, raw);

        var problem = JsonDocument.Parse(raw).RootElement;
        problem.GetProperty("code").GetString().Should().Be("features.unavailable");
        problem.GetProperty("feature").GetString().Should().Be(feature);
    }

    private static async Task PayAsync(HttpClient user, Guid paymentId)
    {
        var paid = await PostAsync(user, $"/api/payments/{paymentId}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
    }

    private static async Task<Guid> PaidPreparationAsync(HttpClient user, string kioskCode, Guid fileId)
    {
        var placed = await PostAsync(user, "/api/prepared-prints/paid", new { kiosk = kioskCode, fileId, options = BlackAndWhite, expectedAmount = 0.30m });
        placed.StatusCode.Should().Be(HttpStatusCode.OK, await placed.Content.ReadAsStringAsync());
        var order = await placed.Content.ReadFromJsonAsync<JsonElement>();
        await PayAsync(user, order.GetProperty("paymentId").GetGuid());

        return order.GetProperty("jobId").GetGuid();
    }

    private static async Task<HttpResponseMessage> RawIntentAsync(HttpClient kiosk, Guid jobId, string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/intents") { Content = JsonContent.Create(new { jobId, method }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await kiosk.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> OrderPreparedAsync(HttpClient kiosk, Guid grant, Guid preparedId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/pending-for-user/prepared/{preparedId}")
        {
            Content = JsonContent.Create(new { options = BlackAndWhite }),
        };

        request.Headers.Add("X-User-Qr-Grant", grant.ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await kiosk.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReleaseAsync(HttpClient kiosk, Guid grant, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/pending-for-user/paid/{jobId}/release");
        request.Headers.Add("X-User-Qr-Grant", grant.ToString());
        return await kiosk.SendAsync(request);
    }

    private static async Task<Dictionary<string, JsonElement>> FlagsAsync(HttpClient admin)
        => (await admin.GetFromJsonAsync<JsonElement>("/api/admin/features"))
            .EnumerateArray()
            .ToDictionary(flag => flag.GetProperty("key").GetString()!, flag => flag.Clone());

    private static async Task PutAsync(HttpClient admin, JsonElement flag, bool defaultEnabled, Dictionary<string, bool> overrides)
    {
        var response = await admin.PutAsJsonAsync($"/api/admin/features/{flag.GetProperty("key").GetString()}", new
        {
            defaultEnabled,
            rolloutPercent = flag.GetProperty("rolloutPercent").GetInt32(),
            kioskOverrides = overrides,
            userAllowlist = flag.GetProperty("userAllowlist").Deserialize<Guid[]>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>The flags are shared by the whole collection: put them back as they were.</summary>
    private static async Task RestoreAsync(HttpClient admin, Dictionary<string, JsonElement> before, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var flag = before[key];
            await PutAsync(admin, flag, flag.GetProperty("defaultEnabled").GetBoolean(), flag.GetProperty("kioskOverrides").Deserialize<Dictionary<string, bool>>()!);
        }
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }
}
