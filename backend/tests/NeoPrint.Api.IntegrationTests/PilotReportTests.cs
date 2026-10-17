using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Analytics;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The pilot report (step 7A, M10 - T-214; plan section 2.17): the kiosk's
/// payment events are kept, the hourly rollup can run again without counting
/// anything twice, and an account sees the figures of its own kiosks only.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PilotReportTests
{
    private const string Password = "PilotReportPassword!2026";

    private readonly NeoPrintApiFixture _fixture;

    public PilotReportTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static DateOnly Today => DateOnly.FromDateTime(NeoPrint.Application.Common.BakuTime.From(DateTime.UtcNow));

    private static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [DockerFact]
    public async Task The_kiosks_payment_events_are_kept_and_count_in_the_funnel()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"plt-{Guid.NewGuid():N}"[..20]);
        var visit = Guid.NewGuid().ToString("N");
        var at = DateTime.UtcNow.AddMinutes(-2);

        var batch = await SendAnalyticsAsync(kiosk.Client,
        [
            Event("screen_view", visit, at, ("screen", "Browse")),
            Event("file_selected", visit, at.AddSeconds(5), ("format", "docx"), ("pages", "2"), ("sizeMb", "0.1")),
            Event("screen_view", visit, at.AddSeconds(9), ("screen", "Options")),
            Event("order_placed", visit, at.AddSeconds(15), ("jobCode", "000000"), ("sheets", "2")),
            Event("payment_started", visit, at.AddSeconds(18), ("method", "BankQr")),
            Event("payment_failed", visit, at.AddSeconds(40), ("method", "BankQr"), ("seconds", "22")),
            Event("payment_expired", visit, at.AddSeconds(60), ("method", "BankQr"), ("seconds", "42")),
            Event("payment_succeeded", visit, at.AddSeconds(80), ("method", "MobileApp"), ("seconds", "20")),
        ]);

        batch.GetProperty("accepted").GetInt32().Should().Be(8, "T-214: the payment events were dropped before");
        batch.GetProperty("rejected").GetInt32().Should().Be(0);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today)}&kioskId={kiosk.KioskId}");

        report.GetProperty("totals").GetProperty("visits").GetInt32().Should().Be(1);
        var steps = report.GetProperty("funnel").EnumerateArray().Select(step => step.GetProperty("count").GetInt32()).ToList();
        steps.Should().Equal(1, 1, 1, 1, 0, 0, 0);
        report.GetProperty("funnel")[4].GetProperty("loss").GetDouble().Should().Be(100, "the order was never paid");
        report.GetProperty("formats")[0].GetProperty("format").GetString().Should().Be("docx");
        report.GetProperty("days").EnumerateArray().Should().ContainSingle().Which.GetProperty("live").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task An_older_kiosks_payment_names_are_kept_under_todays_and_an_unknown_event_is_counted_not_lost()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"plt-{Guid.NewGuid():N}"[..20]);
        var visit = Guid.NewGuid().ToString("N");
        var at = DateTime.UtcNow.AddMinutes(-1);

        var batch = await SendAnalyticsAsync(kiosk.Client,
        [
            Event("payment_method", visit, at, ("method", "BankQr")),
            Event("payment_ended", visit, at.AddSeconds(30), ("status", "Failed")),
            Event("payment_ended", visit, at.AddSeconds(50), ("status", "Expired")),
            Event("brand_new_thing", visit, at.AddSeconds(55), ("x", "1")),
            Event("screen_view", string.Empty, at.AddSeconds(56), ("screen", "Browse")),
        ]);

        batch.GetProperty("accepted").GetInt32().Should().Be(3);
        batch.GetProperty("rejected").GetInt32().Should().Be(2);

        using var services = _fixture.Services.CreateScope();
        var context = services.ServiceProvider.GetRequiredService<MongoContext>();
        var kept = await context.AnalyticsEvents.Find(e => e.KioskId == kiosk.KioskId).ToListAsync();
        kept.Select(e => e.Event).Should().BeEquivalentTo(["payment_started", "payment_failed", "payment_expired"]);
        kept.Single(e => e.Event == "payment_started").Props.Should().ContainKey("method");

        var counted = await context.AnalyticsRejections.Find(row => row.KioskId == kiosk.KioskId).SingleAsync();
        counted.UnknownEvent.Should().Be(1);
        counted.NoSession.Should().Be(1);
        counted.LastUnknownName.Should().Be("brand_new_thing");
    }

    [DockerFact]
    public async Task The_rollup_run_twice_counts_once_and_a_past_day_comes_from_it()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"plt-{Guid.NewGuid():N}"[..20]);
        var past = Today.AddDays(-2);
        var noon = PilotDayBuilder.StartOf(past).AddHours(12);

        await AttemptAsync(kiosk.KioskId, noon, "Completed", null, TimeSpan.FromSeconds(90));
        await AttemptAsync(kiosk.KioskId, noon.AddHours(1), "Interrupted", "printer.paperJam", TimeSpan.Zero);

        await RollupAsync();
        await RollupAsync();

        using (var services = _fixture.Services.CreateScope())
        {
            var stored = await services.ServiceProvider.GetRequiredService<MongoContext>().AnalyticsDays
                .Find(day => day.KioskId == kiosk.KioskId && day.Day == Text(past))
                .ToListAsync();
            var day = stored.Should().ContainSingle("the same day, written over itself").Subject;
            day.Id.Should().Be(PilotDay.IdOf(Text(past), kiosk.KioskId));
            day.PrintsSucceeded.Should().Be(1);
            day.PrintsFailed.Should().Be(1);
        }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Text(past)}&to={Text(Today)}&kioskId={kiosk.KioskId}");
        var totals = report.GetProperty("totals");

        totals.GetProperty("printsSucceeded").GetInt32().Should().Be(1);
        totals.GetProperty("printsFailed").GetInt32().Should().Be(1);
        totals.GetProperty("averagePrintSeconds").GetInt32().Should().Be(90);
        report.GetProperty("printErrors")[0].GetProperty("key").GetString().Should().Be("printer.paperJam");
        report.GetProperty("printErrors")[0].GetProperty("text").GetString().Should().NotBeNullOrWhiteSpace("the reason reads as the server's text");
        report.GetProperty("computedAt").ValueKind.Should().Be(JsonValueKind.String);
        report.GetProperty("days").GetArrayLength().Should().Be(3);

        // Rebuilt on request: the same figures again, and the audit log says who asked.
        var rebuilt = await admin.PostAsJsonAsync("/api/admin/analytics/pilot/rebuild", new { from = Text(past), to = Text(past) });
        rebuilt.StatusCode.Should().Be(HttpStatusCode.OK, await rebuilt.Content.ReadAsStringAsync());
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Text(past)}&to={Text(past)}&kioskId={kiosk.KioskId}"))
            .GetProperty("totals").GetProperty("printsSucceeded").GetInt32().Should().Be(1);
        (await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit-logs?action=analytics.rebuilt"))
            .GetProperty("total").GetInt64().Should().BeGreaterThan(0);

        // Days whose events are no longer kept are not worked out again; nor are days to come.
        var tooOld = Text(Today.AddDays(-400));
        (await admin.PostAsJsonAsync("/api/admin/analytics/pilot/rebuild", new { from = tooOld, to = Text(Today.AddDays(-399)) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today.AddDays(1))}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_sees_its_figures_only()
    {
        if (Skip) { return; }

        using var mine = await TestKiosks.ActivatedAsync(_fixture, $"plt-{Guid.NewGuid():N}"[..20]);
        using var theirs = await TestKiosks.ActivatedAsync(_fixture, $"plt-{Guid.NewGuid():N}"[..20]);
        var now = DateTime.UtcNow.AddMinutes(-1);

        await AttemptAsync(mine.KioskId, now, "Completed", null, TimeSpan.FromSeconds(30));
        await AttemptAsync(theirs.KioskId, now, "Completed", null, TimeSpan.FromSeconds(30));
        await AttemptAsync(theirs.KioskId, now, "Completed", null, TimeSpan.FromSeconds(30));

        using var scoped = await AccountAsync(KioskScope.Only(mine.KioskId), Permissions.AnalyticsView);
        var report = await scoped.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today)}");

        report.GetProperty("totals").GetProperty("printsSucceeded").GetInt32().Should().Be(1, "the other kiosk's two prints are not this account's");
        report.TryGetProperty("appFormats", out var app).Should().BeFalse("the app's uploads belong to no kiosk: {0}", app);
        (await scoped.GetAsync($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today)}&kioskId={theirs.KioskId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var blind = await AccountAsync(KioskScope.Everything(), Permissions.KiosksView, Permissions.ReportsView);
        (await blind.GetAsync($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today)}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blind.PostAsJsonAsync("/api/admin/analytics/pilot/rebuild", new { from = Text(Today), to = Text(Today) }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Text(Today)}&to={Text(Today)}&kioskId={theirs.KioskId}"))
            .GetProperty("totals").GetProperty("printsSucceeded").GetInt32().Should().Be(2);
    }

    private static object Event(string name, string session, DateTime at, params (string Key, string Value)[] props)
        => new { @event = name, sessionId = session, at, props = props.ToDictionary(p => p.Key, p => p.Value) };

    private static async Task<JsonElement> SendAnalyticsAsync(HttpClient kiosk, object[] events)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/kiosks/me/analytics") { Content = JsonContent.Create(new { events }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await kiosk.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A print attempt as the kiosk would have reported it - the rollup reads nothing else of a print.</summary>
    private async Task AttemptAsync(Guid kioskId, DateTime finished, string status, string? error, TimeSpan took)
    {
        using var services = _fixture.Services.CreateScope();
        await services.ServiceProvider.GetRequiredService<MongoContext>().PrintAttempts.InsertOneAsync(new PrintAttempt
        {
            JobId = Guid.NewGuid(),
            AttemptNo = 1,
            KioskId = kioskId,
            StartedAt = finished - took,
            FinishedAt = finished,
            Status = status,
            ErrorCode = error,
            ReportedSheets = 1,
            UpdatedAt = finished,
        });
    }

    private async Task RollupAsync()
    {
        using var services = _fixture.Services.CreateScope();
        await services.ServiceProvider.GetRequiredService<ISender>().Send(new RunAnalyticsRollupCommand());
    }

    private async Task<HttpClient> AccountAsync(KioskScope scope, params string[] permissions)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Pilot role {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var services = _fixture.Services.CreateScope();
        var hasher = services.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();
        var account = new AdminUser
        {
            Email = $"pilot.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Pilot report",
            RoleIds = [(await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()],
            KioskScope = scope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, Password);
        await services.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }
}
