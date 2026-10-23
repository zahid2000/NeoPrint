using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Admin;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Test accounts (T-300): what an account marked as a test account orders and
/// pays is left out of the pilot report and the revenue report - the past days
/// too - and the mark is a SuperAdmin's to set, with a reason, in the audit log.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TestAccountTests
{
    private readonly NeoPrintApiFixture _fixture;

    public TestAccountTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_orders_of_a_test_account_are_not_in_the_pilot_or_the_revenue_report_and_come_back_when_the_mark_is_taken_away()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "test-account");
        using var tester = await TestUsers.ConfirmedAsync(_fixture);
        using var customer = await TestUsers.ConfirmedAsync(_fixture);

        var (_, testerPrice) = await PaidOrderAsync(kiosk, tester);
        var (_, customerPrice) = await PaidOrderAsync(kiosk, customer);

        var marked = await admin.PostAsJsonAsync($"/api/admin/users/{tester.Id}/test-account", new { isTest = true, reason = "QA telefonu" });
        marked.StatusCode.Should().Be(HttpStatusCode.OK, await marked.Content.ReadAsStringAsync());
        (await marked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isTestAccount").GetBoolean().Should().BeTrue();

        // Only the customer's order counts, in both reports.
        (await RevenueAsync(admin, kiosk)).Should().Be(customerPrice);
        var pilot = await PilotAsync(admin, kiosk);
        pilot.Orders.Should().Be(1, "the test account's order is left out");
        pilot.Sales.Should().Be(customerPrice);

        // The mark taken away: the order counts again - the kept day was worked out again.
        (await admin.PostAsJsonAsync($"/api/admin/users/{tester.Id}/test-account", new { isTest = false, reason = "Səhv işarələnmişdi" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await RevenueAsync(admin, kiosk)).Should().Be(customerPrice + testerPrice);
        (await PilotAsync(admin, kiosk)).Orders.Should().Be(2);

        // Both changes are in the audit log with their reasons.
        using var scope = _fixture.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(entry => entry.EntityId == tester.Id.ToString())
            .ToListAsync();
        audit.Select(entry => (entry.Action, entry.Reason)).Should().Contain(
            [("users.testAccount.set", "QA telefonu"), ("users.testAccount.clear", "Səhv işarələnmişdi")]);
    }

    [DockerFact]
    public async Task Marking_a_test_account_takes_its_own_permission_and_a_reason()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        (await admin.PostAsJsonAsync($"/api/admin/users/{user.Id}/test-account", new { isTest = true, reason = " " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "what leaves the reports needs a reason");

        // The Admin role does not have it: given by hand, like the background jobs (T-284).
        using var scope = _fixture.Services.CreateScope();
        var role = await scope.ServiceProvider.GetRequiredService<MongoContext>().AdminRoles.Find(r => r.Name == "Admin").SingleAsync();
        role.Permissions.Should().NotContain(Permissions.UsersMarkTest);
    }

    private async Task<(Guid JobId, decimal Price)> PaidOrderAsync(TestKiosk kiosk, TestUser user)
    {
        using var topUp = new HttpRequestMessage(HttpMethod.Post, "/api/wallet/top-up") { Content = JsonContent.Create(new { amount = 5m }) };
        topUp.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(topUp)).EnsureSuccessStatusCode();

        var job = await PlaceAsync(kiosk.Client);
        var jobId = job.GetProperty("id").GetGuid();
        var intent = await IntentAsync(kiosk.Client, jobId, "MobileApp");

        using var pay = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/{intent.GetProperty("id").GetGuid()}/pay-with-app")
        {
            Content = JsonContent.Create(new { source = "Wallet", useBonus = false }),
        };
        pay.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await user.Client.SendAsync(pay)).EnsureSuccessStatusCode();

        await EventuallyAsync(
            async () =>
            {
                using var scope = _fixture.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.Find(j => j.Id == jobId).SingleAsync();
            },
            stored => stored.Status == PrintJobStatus.Queued,
            "the paid order joining the queue");

        return (jobId, intent.GetProperty("amount").GetDecimal());
    }

    private static string Today => DateOnly.FromDateTime(NeoPrint.Application.Common.BakuTime.From(DateTime.UtcNow)).ToString("yyyy-MM-dd");

    private static async Task<decimal> RevenueAsync(HttpClient admin, TestKiosk kiosk)
        => (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/reports/revenue?from={Today}&to={Today}&kioskId={kiosk.KioskId}"))
            .GetProperty("totals").GetProperty("sales").GetDecimal();

    private static async Task<(int Orders, decimal Sales)> PilotAsync(HttpClient admin, TestKiosk kiosk)
    {
        var totals = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/analytics/pilot?from={Today}&to={Today}&kioskId={kiosk.KioskId}"))
            .GetProperty("totals");

        return (totals.GetProperty("orders").GetInt32(), totals.GetProperty("sales").GetDecimal());
    }
}
