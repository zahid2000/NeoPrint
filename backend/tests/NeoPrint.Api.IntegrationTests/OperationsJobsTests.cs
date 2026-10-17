using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using MediatR;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Operations;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Operations;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Wallets;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The system's own checks (step 7A, M11 - T-215), on the real stores: data
/// broken on purpose raises its incident, data that agrees raises none, and
/// every job run twice gives the same result. The Hangfire panel is for a
/// SuperAdmin with a ticket, and what is done in it is audited.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class OperationsJobsTests
{
    private const string Password = "OperationsPassword!2026";

    private readonly NeoPrintApiFixture _fixture;

    public OperationsJobsTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private async Task<JobRun> SendAsync(IRequest<JobRun> command)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private async Task<List<Incident>> IncidentsAsync(string dedupKey)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents.Find(i => i.DedupKey == dedupKey).ToListAsync();
    }

    private async Task<long> AuditCountAsync(string action, string entityId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .CountDocumentsAsync(Builders<NeoPrint.Domain.Audit.AuditLog>.Filter.Eq(e => e.Action, action) & Builders<NeoPrint.Domain.Audit.AuditLog>.Filter.Eq(e => e.EntityId, entityId));
    }

    // -----------------------------------------------------------------------
    // Dead-letter queues
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_message_in_a_dead_letter_queue_is_one_high_incident_with_its_age_and_closes_when_the_queue_is_empty()
    {
        if (Skip) { return; }

        var queue = $"it-{Guid.NewGuid():N}"[..12] + "_error";
        using var broker = Management();
        (await broker.PutAsJsonAsync($"api/queues/%2F/{queue}", new { durable = false, auto_delete = false })).EnsureSuccessStatusCode();

        try
        {
            var failedAt = DateTime.UtcNow.AddHours(-3);
            (await broker.PostAsJsonAsync("api/exchanges/%2F/amq.default/publish", new
            {
                properties = new { headers = new Dictionary<string, object> { ["MT-Fault-Timestamp"] = failedAt.ToString("O") } },
                routing_key = queue,
                payload = "{}",
                payload_encoding = "string",
            })).EnsureSuccessStatusCode();

            // The management API counts every few seconds.
            await PaymentTestKit.EventuallyAsync(
                async () => (await broker.GetFromJsonAsync<JsonElement>($"api/queues/%2F/{queue}")).TryGetProperty("messages", out var count) ? count.GetInt64() : 0,
                count => count == 1,
                "the broker to count the message");

            var first = await SendAsync(new RunDeadLetterCheckCommand());
            var second = await SendAsync(new RunDeadLetterCheckCommand());

            first.Succeeded.Should().BeTrue(first.Error);
            var seen = first.Queues!.Should().ContainSingle(q => q.Name == queue).Subject;
            seen.Messages.Should().Be(1);
            seen.OldestAt.Should().BeCloseTo(failedAt, TimeSpan.FromSeconds(2), "the age comes from MassTransit's fault header");
            second.Queues!.Single(q => q.Name == queue).Messages.Should().Be(1, "looking at the oldest message puts it back");

            var incident = (await IncidentsAsync(RunDeadLetterCheckHandler.KeyOf(queue))).Should().ContainSingle("run twice, one incident").Subject;
            incident.Severity.Should().Be(IncidentSeverity.High);
            incident.Open.Should().BeTrue();

            (await broker.DeleteAsync($"api/queues/%2F/{queue}/contents")).EnsureSuccessStatusCode();
            await PaymentTestKit.EventuallyAsync(
                async () => (await broker.GetFromJsonAsync<JsonElement>($"api/queues/%2F/{queue}")).TryGetProperty("messages", out var count) ? count.GetInt64() : -1,
                count => count == 0,
                "the broker to count the queue empty");
            await SendAsync(new RunDeadLetterCheckCommand());

            (await IncidentsAsync(RunDeadLetterCheckHandler.KeyOf(queue))).Should().ContainSingle().Which.Open.Should().BeFalse("an empty queue closes it");
        }
        finally
        {
            await broker.DeleteAsync($"api/queues/%2F/{queue}");
        }
    }

    // -----------------------------------------------------------------------
    // Wallet ledger
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_balance_the_ledger_does_not_add_up_to_is_one_critical_incident_and_one_audit_entry_and_is_not_corrected()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var now = DateTime.UtcNow;

        var broken = Wallet.For(Guid.NewGuid());
        broken.Post(WalletTransactionType.TopUp, Money.FromAzn(5), 0, "it-topup", $"it:{Guid.NewGuid():N}", null, now);
        await context.Wallets.InsertOneAsync(broken); // the ledger line is not written

        var sound = Wallet.For(Guid.NewGuid());
        var line = sound.Post(WalletTransactionType.TopUp, Money.FromAzn(3), 0, "it-topup", $"it:{Guid.NewGuid():N}", null, now);
        await context.Wallets.InsertOneAsync(sound);
        await context.WalletTransactions.InsertOneAsync(line);

        var first = await SendAsync(new RunWalletLedgerCheckCommand());
        await SendAsync(new RunWalletLedgerCheckCommand());

        first.Findings.Should().Contain(f => f.Kind == "walletMismatch" && f.Ref == broken.UserId.ToString() && f.Amount == 5m && f.Other == 0m);
        first.Findings.Should().NotContain(f => f.Ref == sound.UserId.ToString());

        var incident = (await IncidentsAsync(RunWalletLedgerCheckHandler.KeyOf(broken.UserId))).Should().ContainSingle().Subject;
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        (await IncidentsAsync(RunWalletLedgerCheckHandler.KeyOf(sound.UserId))).Should().BeEmpty();
        (await AuditCountAsync("reconciliation.walletMismatch", broken.UserId.ToString())).Should().Be(1, "found once, audited once");

        (await context.Wallets.Find(w => w.Id == broken.Id).SingleAsync()).Balance.Should().Be(Money.FromAzn(5), "nothing is corrected automatically");
    }

    // -----------------------------------------------------------------------
    // Payments against their orders, refunds and receipts
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_paid_order_cancelled_without_a_refund_is_one_critical_incident_and_a_printed_one_is_none()
    {
        if (Skip) { return; }

        var kiosk = await PaymentTestKit.ReadyKioskAsync(_fixture, "chk");
        var (lostJob, lostPayment, _) = await PaymentTestKit.PaidBankJobAsync(_fixture, kiosk, pages: 1);
        var (goodJob, goodPayment, _) = await PaymentTestKit.PaidBankJobAsync(_fixture, kiosk, pages: 1);

        await PaymentTestKit.ReportAsync(kiosk.Client, goodJob, "Printing");
        await PaymentTestKit.ReportAsync(kiosk.Client, goodJob, "Completed", sides: 1, sheets: 1);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        // Both have their receipts by now (the consumer issues them) - only the lost money is wrong.
        foreach (var payment in new[] { lostPayment, goodPayment })
        {
            await PaymentTestKit.EventuallyAsync(
                () => context.FiscalReceipts.Find(r => r.PaymentId == payment).AnyAsync(), found => found, "the sale's receipt");
        }

        // Broken on purpose: the order is cancelled behind the system's back, and both were paid two hours ago.
        await context.PrintJobs.UpdateOneAsync(Builders<PrintJob>.Filter.Eq(j => j.Id, lostJob), Builders<PrintJob>.Update.Set("Status", "Cancelled"));
        await context.Payments.UpdateManyAsync(
            Builders<NeoPrint.Domain.Payments.Payment>.Filter.In(p => p.Id, new[] { lostPayment, goodPayment }),
            Builders<NeoPrint.Domain.Payments.Payment>.Update.Set("PaidAt", DateTime.UtcNow.AddHours(-2)));

        var first = await SendAsync(new RunPaymentConsistencyCheckCommand());
        await SendAsync(new RunPaymentConsistencyCheckCommand());

        first.Findings.Should().Contain(f => f.Kind == MoneyConsistency.PaidNotPrinted && f.Ref == lostPayment.ToString());
        first.Findings.Should().NotContain(f => f.Ref == goodPayment.ToString());

        var key = $"money-check:{MoneyConsistency.PaidNotPrinted}:{lostPayment}";
        var incident = (await IncidentsAsync(key)).Should().ContainSingle().Subject;
        incident.Severity.Should().Be(IncidentSeverity.Critical);
        incident.KioskId.Should().Be(kiosk.KioskId);
        (await AuditCountAsync($"reconciliation.{MoneyConsistency.PaidNotPrinted}", lostPayment.ToString())).Should().Be(1);
        (await context.Refunds.Find(r => r.PaymentId == lostPayment).AnyAsync()).Should().BeFalse("nothing is refunded automatically");
    }

    // -----------------------------------------------------------------------
    // File cleanup
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_cleanup_deletes_only_unused_old_objects_tries_first_and_never_touches_what_an_order_or_prepared_print_holds()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var user = Guid.NewGuid();

        async Task<StoredObject> WriteAsync(Guid file, string name)
        {
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 it"));
            return await storage.WriteAsync(StorageBucket.Files, $"files/{user:N}/{file:N}/{name}", content, "application/pdf");
        }

        var orphan = await WriteAsync(Guid.NewGuid(), "print-v9.pdf");

        // A file a "Kiosk üçün hazırla" order holds, and an object an open order prints from.
        var prepared = Guid.NewGuid();
        var heldByPrepared = await WriteAsync(prepared, "print-v1.pdf");
        await context.Database.GetCollection<BsonDocument>("prepared_prints").InsertOneAsync(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["FileId"] = new BsonBinaryData(prepared, GuidRepresentation.Standard),
            ["RemovedAt"] = BsonNull.Value,
        });
        var heldByOrder = await WriteAsync(Guid.NewGuid(), "print.pdf");
        await context.Database.GetCollection<BsonDocument>("print_jobs").InsertOneAsync(new BsonDocument
        {
            ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
            ["Status"] = nameof(PrintJobStatus.Queued),
            ["PrintFile"] = new BsonDocument { ["Key"] = heldByOrder.Key },
        });

        // Written a moment ago: within the default grace nothing goes.
        var recent = await SendAsync(new RunFileCleanupCommand(DryRun: false));
        recent.Findings.Should().NotContain(f => f.Ref == orphan.Key);
        (await storage.ExistsAsync(orphan, StorageBucket.Files)).Should().BeTrue();

        var dry = await SendAsync(new RunFileCleanupCommand(DryRun: true, GraceHours: 0));
        dry.DryRun.Should().BeTrue();
        dry.Findings.Should().Contain(f => f.Kind == "wouldDelete" && f.Ref == orphan.Key);
        dry.Findings.Should().NotContain(f => f.Ref == heldByPrepared.Key || f.Ref == heldByOrder.Key);
        (await storage.ExistsAsync(orphan, StorageBucket.Files)).Should().BeTrue("a dry run deletes nothing");

        var real = await SendAsync(new RunFileCleanupCommand(DryRun: false, GraceHours: 0));
        real.Findings.Should().Contain(f => f.Kind == "deleted" && f.Ref == orphan.Key);
        real.Counts["protected"].Should().BeGreaterThanOrEqualTo(2);
        (await storage.ExistsAsync(orphan, StorageBucket.Files)).Should().BeFalse();
        (await storage.ExistsAsync(heldByPrepared, StorageBucket.Files)).Should().BeTrue();
        (await storage.ExistsAsync(heldByOrder, StorageBucket.Files)).Should().BeTrue();
        (await context.AuditLogs.CountDocumentsAsync(e => e.Action == "files.cleanup.deleted" && e.Before == orphan.Key)).Should().Be(1);

        var again = await SendAsync(new RunFileCleanupCommand(DryRun: false, GraceHours: 0));
        again.Findings.Should().NotContain(f => f.Ref == orphan.Key, "deleted once; the second run finds nothing of it");
    }

    // -----------------------------------------------------------------------
    // The admin page and the Hangfire panel
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_jobs_page_is_for_an_every_kiosk_account_with_its_permission()
    {
        if (Skip) { return; }

        await SendAsync(new RunWalletLedgerCheckCommand());
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var view = await admin.GetFromJsonAsync<JsonElement>("/api/admin/system/jobs");
        view.GetProperty("runs").TryGetProperty("wallet-ledger", out _).Should().BeTrue();
        view.GetProperty("analytics").GetProperty("week").ValueKind.Should().Be(JsonValueKind.Number);

        using var scoped = await AccountAsync(KioskScope.Only(Guid.NewGuid()), Permissions.SystemJobsView);
        (await scoped.GetAsync("/api/admin/system/jobs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var blind = await AccountAsync(KioskScope.Everything(), Permissions.AuditView);
        (await blind.GetAsync("/api/admin/system/jobs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task The_hangfire_panel_opens_for_a_superadmin_ticket_only_and_what_is_done_there_is_audited()
    {
        if (Skip) { return; }

        using (var stranger = _fixture.CreateApiClient())
        {
            (await stranger.GetAsync("/api/admin/hangfire")).StatusCode.Should().NotBe(HttpStatusCode.OK, "no ticket, no panel");
        }

        using var notSuper = await AccountAsync(KioskScope.Everything(), Permissions.SystemJobsView);
        (await notSuper.PostAsync("/api/admin/hangfire-session", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await notSuper.GetAsync("/api/admin/hangfire")).StatusCode.Should().NotBe(HttpStatusCode.OK);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var session = await admin.PostAsync("/api/admin/hangfire-session", null);
        session.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = session.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("neoprint_hangfire=", StringComparison.Ordinal)).ToLowerInvariant();
        cookie.Should().Contain("path=/api/admin/hangfire").And.Contain("httponly").And.Contain("samesite=strict");

        var page = await admin.GetAsync("/api/admin/hangfire/recurring");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("analytics-rollup");

        // "Trigger now", as the panel's own button sends it.
        using var trigger = new HttpRequestMessage(HttpMethod.Post, "/api/admin/hangfire/recurring/trigger")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("jobs[]", "analytics-rollup")]),
        };

        if (AntiforgeryHeader().Match(html) is { Success: true } header && AntiforgeryToken().Match(html) is { Success: true } token)
        {
            trigger.Headers.Add(header.Groups[1].Value, WebUtility.HtmlDecode(token.Groups[1].Value));
        }

        var triggered = await admin.SendAsync(trigger);
        triggered.StatusCode.Should().Be(HttpStatusCode.NoContent, await triggered.Content.ReadAsStringAsync());

        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit-logs?action=hangfire.&pageSize=200");
        var actions = audit.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("action").GetString(), item.TryGetProperty("entityId", out var id) ? id.GetString() : null)).ToList();
        actions.Should().Contain(("hangfire.opened", null));
        actions.Should().Contain(("hangfire.action", "analytics-rollup"));
    }

    [GeneratedRegex("name=\"csrf-header\" content=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryHeader();

    [GeneratedRegex("name=\"csrf-token\" content=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();

    // -----------------------------------------------------------------------

    private HttpClient Management()
    {
        var broker = new Uri(_fixture.Services.GetRequiredService<IConfiguration>()["NEOPRINT_RABBITMQ_URI"]!);
        var user = broker.UserInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        var client = new HttpClient { BaseAddress = new Uri(_fixture.RabbitManagementUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user[0]}:{user[1]}")));
        return client;
    }

    private async Task<HttpClient> AccountAsync(KioskScope scope, params string[] permissions)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Ops role {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var services = _fixture.Services.CreateScope();
        var hasher = services.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();
        var account = new AdminUser
        {
            Email = $"ops.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Operations",
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
