using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Who may call what (T-295, plan sections 8A.2 and 11): every endpoint says
/// who may call it, and what step 7A added - its endpoints, its panel events
/// and the Hangfire panel - is tried as each kind of caller there is: the four
/// seeded roles, an account that looks after one kiosk, a signed-in app user,
/// a kiosk and nobody at all.
/// </summary>
/// <remarks>
/// The expected permission of each endpoint is written here, not read from the
/// attribute, so that loosening one is a failing test and not a quiet change.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class PermissionMatrixTests
{
    private const string Password = "MatrixPassword!2026";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The pilot report's last seven Baku days.</summary>
    private static readonly string LastWeek = string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"from={DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4).AddDays(-6)):yyyy-MM-dd}&to={DateOnly.FromDateTime(DateTime.UtcNow.AddHours(4)):yyyy-MM-dd}");

    private readonly NeoPrintApiFixture _fixture;

    public PermissionMatrixTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // Every endpoint
    // -----------------------------------------------------------------------

    /// <summary>
    /// There is no fallback policy: an endpoint without an attribute would be
    /// open to anybody. These are the ones that are open on purpose.
    /// </summary>
    private static readonly string[] AnonymousOnPurpose =
    [
        // Signing in, registering, recovering an account.
        "POST api/auth/register", "POST api/auth/login", "POST api/auth/refresh", "POST api/auth/logout",
        "POST api/auth/confirm-email", "POST api/auth/resend-confirmation", "POST api/auth/admin/login", "POST api/auth/admin/refresh",
        "POST api/auth/admin/logout",
        // A kiosk that has no token yet (plan section 7.11): its signed challenge is the check.
        "POST api/devices/register", "POST api/devices/challenge", "POST api/devices/token",
        // What the app and the kiosk show before anybody signs in.
        "GET api/legal/{docType}", "GET api/map/config", "GET api/features", "GET api/files/formats",
        // The payment provider's callbacks; their signature is the check.
        "POST api/payments/callback/{provider}",
        // By a random id from a QR, with no personal data (plan sections 2.9, 2.12).
        "GET api/payments/{id:guid}/fiscal", "GET api/refunds/{id:guid}/public",
        // An anonymous customer's claim, limited per IP (plan section 2.5).
        "POST api/support/claims", "POST api/refunds/{id:guid}/claim",
        // The data export: the signed link stands in for the session (plan section 2.13).
        "GET api/exports/download",
        // Development only ([DevelopmentOnly]: 404 everywhere else).
        "POST api/dev/storage/orphan", "POST api/dev/payments/{id:guid}/simulate-success", "POST api/dev/payments/{id:guid}/simulate-fail",
        "POST api/dev/mock-bank/create-fails", "POST api/dev/mock-bank/refunds-fail", "POST api/dev/mock-card/outcome",
        "POST api/dev/refunds/{id:guid}/simulate-fail", "POST api/dev/fiscal/{id:guid}/simulate-fail", "POST api/dev/mock-fiscal/mode",
    ];

    [DockerFact]
    public void Every_endpoint_says_who_may_call_it_and_the_open_ones_are_the_known_few()
    {
        if (Skip) { return; }

        var open = new List<string>();
        var adminWithoutAdminToken = new List<string>();

        foreach (var endpoint in Endpoints())
        {
            var name = Name(endpoint);
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null || authorize.Count == 0)
            {
                open.Add(name);
                continue;
            }

            // Everything under api/admin is for an admin token: a permission,
            // or the admin policy itself (the account's own page).
            if (endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("api/admin/", StringComparison.Ordinal)
                && !authorize.Any(data => data is HasPermissionAttribute || data.Policy == AuthorizationPolicies.Admin))
            {
                adminWithoutAdminToken.Add(name);
            }
        }

        open.Should().BeEquivalentTo(AnonymousOnPurpose, "an endpoint open to anybody has to be meant to be");
        adminWithoutAdminToken.Should().BeEmpty("an admin endpoint takes an admin token");
    }

    [DockerFact]
    public void Each_endpoint_of_step_7A_asks_for_the_permission_written_here()
    {
        if (Skip) { return; }

        var declared = Endpoints().GroupBy(Name).ToDictionary(group => group.Key, group => group.First().Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data is HasPermissionAttribute permission ? permission.Permission : "policy:" + data.Policy)
            .ToList());

        foreach (var call in AdminCalls)
        {
            declared.Should().ContainKey(call.Endpoint);
            declared[call.Endpoint].Should().Equal([call.Permission], call.Endpoint);
        }

        foreach (var call in KioskCalls)
        {
            declared[call.Endpoint].Should().Equal(["policy:" + AuthorizationPolicies.Kiosk], call.Endpoint);
        }

        declared["POST api/print-jobs/my/{id:guid}/cancel"].Should().Equal(["policy:" + AuthorizationPolicies.User]);
    }

    // -----------------------------------------------------------------------
    // Step 7A: role x endpoint
    // -----------------------------------------------------------------------

    /// <summary>Which id a route takes: the kiosk of the test, or one that names nothing.</summary>
    private enum Target
    {
        None,
        TheirKiosk,
        Nothing,
    }

    /// <param name="Permission">What the endpoint asks for.</param>
    /// <param name="Body">Sent as JSON; <c>{kiosk}</c> and <c>{item}</c> in it are filled in.</param>
    private sealed record Call(string Method, string Template, string Permission, Target Target = Target.None, string? Body = null, string? Query = null)
    {
        public string Endpoint => $"{Method} {Template}";
    }

    private static readonly Call[] AdminCalls =
    [
        new("GET", "api/admin/analytics/pilot", Permissions.AnalyticsView, Query: LastWeek),
        new("POST", "api/admin/analytics/pilot/rebuild", Permissions.AnalyticsView, Query: LastWeek),
        new("GET", "api/admin/audit-logs", Permissions.AuditView),
        new("GET", "api/admin/incidents", Permissions.IncidentsView),
        new("GET", "api/admin/incidents/{id:guid}", Permissions.IncidentsView, Target.Nothing),
        new("POST", "api/admin/incidents/{id:guid}/acknowledge", Permissions.IncidentsAcknowledge, Target.Nothing),
        new("POST", "api/admin/incidents/{id:guid}/resolve", Permissions.IncidentsResolve, Target.Nothing, """{"note":"matrix"}"""),
        new("GET", "api/admin/kiosks/{id:guid}/supplies", Permissions.SuppliesView, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/paper/refill", Permissions.PaperRefill, Target.TheirKiosk, """{"paper":"A4","mode":"Add","sheets":1}"""),
        new("POST", "api/admin/kiosks/{id:guid}/supplies/replaced", Permissions.PaperRefill, Target.TheirKiosk, """{"resource":"Toner","variant":"Black Cartridge"}"""),
        new("GET", "api/admin/kiosks/{id:guid}/telemetry", Permissions.KiosksView, Target.TheirKiosk),
        new("GET", "api/admin/kiosks/{id:guid}/telemetry/history", Permissions.KiosksView, Target.TheirKiosk),
        new("GET", "api/admin/kiosks/{id:guid}/printers/telemetry", Permissions.PrintersView, Target.TheirKiosk),
        new("GET", "api/admin/kiosks/{id:guid}/printers/telemetry/history", Permissions.PrintersView, Target.TheirKiosk, Query: "printer=matrix"),
        new("GET", "api/admin/kiosks/{id:guid}/printers/telemetry/events", Permissions.PrintersView, Target.TheirKiosk, Query: "printer=matrix"),
        new("POST", "api/admin/kiosks/{id:guid}/pause", Permissions.KioskCommandsPause, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/resume", Permissions.KioskCommandsPause, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/restart-app", Permissions.KioskCommandsRestart, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/reboot", Permissions.KioskCommandsReboot, Target.TheirKiosk),
        new("GET", "api/admin/kiosks/{id:guid}/commands", Permissions.KiosksView, Target.TheirKiosk),
        new("GET", "api/admin/printers", Permissions.PrintersView),
        new("GET", "api/admin/supply-rules", Permissions.SuppliesView),
        new("POST", "api/admin/supply-rules", Permissions.SupplyRulesManage, Body: Rule("{kiosk}")),
        new("PUT", "api/admin/supply-rules/{id:guid}", Permissions.SupplyRulesManage, Target.Nothing, Rule("{kiosk}")),
        new("DELETE", "api/admin/supply-rules/{id:guid}", Permissions.SupplyRulesManage, Target.Nothing),
        new("GET", "api/admin/supply-alerts", Permissions.SuppliesView),
        new("GET", "api/admin/service-tasks", Permissions.ServiceTasksView),
        new("GET", "api/admin/service-tasks/summary", Permissions.ServiceTasksView),
        new("GET", "api/admin/service-tasks/{id:guid}", Permissions.ServiceTasksView, Target.Nothing),
        new("GET", "api/admin/service-tasks/assignees", Permissions.ServiceTasksManage, Query: "kioskId={kiosk}"),
        new("POST", "api/admin/service-tasks", Permissions.ServiceTasksManage, Body: """{"kioskId":"{kiosk}","type":"Cleaning"}"""),
        new("PUT", "api/admin/service-tasks/{id:guid}", Permissions.ServiceTasksManage, Target.Nothing, """{"priority":"Normal"}"""),
        new("POST", "api/admin/service-tasks/{id:guid}/assign", Permissions.ServiceTasksManage, Target.Nothing, """{"assignTo":null}"""),
        new("POST", "api/admin/service-tasks/{id:guid}/accept", Permissions.ServiceTasksComplete, Target.Nothing),
        new("POST", "api/admin/service-tasks/{id:guid}/start", Permissions.ServiceTasksComplete, Target.Nothing),
        new("POST", "api/admin/service-tasks/{id:guid}/complete", Permissions.ServiceTasksComplete, Target.Nothing, "{}"),
        new("POST", "api/admin/service-tasks/{id:guid}/cancel", Permissions.ServiceTasksManage, Target.Nothing, """{"reason":"matrix"}"""),
        new("GET", "api/admin/inventory/items", Permissions.InventoryView),
        new("POST", "api/admin/inventory/items", Permissions.InventoryManage, Body: "{}"),
        new("PUT", "api/admin/inventory/items/{id:guid}", Permissions.InventoryManage, Target.Nothing, "{}"),
        new("GET", "api/admin/inventory/movements", Permissions.InventoryView),
        new("POST", "api/admin/inventory/items/{id:guid}/take", Permissions.OperatorPanel, Target.Nothing, """{"quantity":1}"""),
        new("POST", "api/admin/inventory/movements", Permissions.InventoryManage, Body: """{"itemId":"{item}","type":"Purchase","quantity":1,"kioskId":"{kiosk}"}"""),
        new("GET", "api/admin/system/jobs", Permissions.SystemJobsView),
        new("POST", "api/admin/hangfire-session", Permissions.SystemJobsView),
        new("GET", "api/admin/operator/kiosks", Permissions.OperatorPanel),
        new("GET", "api/admin/operator/kiosks/{id:guid}", Permissions.OperatorPanel, Target.TheirKiosk),

        // Step 7B, M16: releases, rollouts and the agent's commands (T-317).
        new("GET", "api/admin/releases", Permissions.ReleasesView),
        new("POST", "api/admin/releases", Permissions.ReleasesManage, Body: "{}"),
        new("POST", "api/admin/releases/{id:guid}/promote", Permissions.ReleasesManage, Target.Nothing),
        new("POST", "api/admin/releases/{id:guid}/withdraw", Permissions.ReleasesManage, Target.Nothing, """{"reason":"matrix"}"""),
        new("POST", "api/admin/rollouts", Permissions.ReleasesManage, Body: $$"""{"releaseId":"{{Guid.Empty}}","target":"All"}"""),
        new("POST", "api/admin/rollouts/{id:guid}/pause", Permissions.ReleasesManage, Target.Nothing),
        new("POST", "api/admin/rollouts/{id:guid}/resume", Permissions.ReleasesManage, Target.Nothing),
        new("POST", "api/admin/rollouts/{id:guid}/complete", Permissions.ReleasesManage, Target.Nothing),
        new("POST", "api/admin/rollouts/{id:guid}/rollback", Permissions.ReleasesManage, Target.Nothing),
        new("GET", "api/admin/kiosks/versions", Permissions.ReleasesView),
        new("PUT", "api/admin/kiosks/{id:guid}/update-channel", Permissions.ReleasesManage, Target.TheirKiosk, """{"channel":"Stable"}"""),
        new("POST", "api/admin/kiosks/{id:guid}/update-now", Permissions.KioskCommandsUpdate, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/rollback-version", Permissions.KioskCommandsUpdate, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/reapply-provisioning", Permissions.KioskCommandsUpdate, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/restart-agent", Permissions.KioskCommandsRestart, Target.TheirKiosk),
        new("POST", "api/admin/kiosks/{id:guid}/upload-logs", Permissions.KioskLogsDownload, Target.TheirKiosk),

        // Step 7B, M17 (T-321): the logs a kiosk uploaded.
        new("GET", "api/admin/kiosks/{id:guid}/logs", Permissions.KioskLogsDownload, Target.TheirKiosk),
        new("GET", "api/admin/kiosks/{id:guid}/logs/{logId:guid}", Permissions.KioskLogsDownload, Target.TheirKiosk),
    ];

    /// <summary>What names the other kiosk in its body: for the scoped account the same as its id in the route.</summary>
    private static bool NamesTheirKiosk(Call call)
        => call.Target == Target.TheirKiosk || (call.Body?.Contains("{kiosk}", StringComparison.Ordinal) ?? false);

    private static string Rule(string kiosk)
        => $$"""{"name":"Matrix","kioskIds":["{{kiosk}}"],"resource":"PaperA4","resourceVariant":null,"unit":"Sheets","warningThreshold":2,"criticalThreshold":1,"channels":["AdminPanel"],"createServiceTask":false,"taskPriority":"Normal","isActive":true}""";

    private static readonly Call[] KioskCalls =
    [
        new("POST", "api/kiosks/me/telemetry", "kiosk", Body: "{}"),
        new("POST", "api/kiosks/me/printers/telemetry", "kiosk", Body: "{}"),
        new("POST", "api/kiosks/me/commands/{id:guid}/ack", "kiosk", Target.Nothing),
        new("GET", "api/kiosks/me/sessions", "kiosk"),
        new("POST", "api/incidents", "kiosk", Body: """{"type":"PaperJam"}"""),

        // Step 7B, M16 (T-317).
        new("GET", "api/kiosks/me/update", "kiosk"),
        new("POST", "api/kiosks/me/update-status", "kiosk", Body: """{"component":"App","version":"1.0.0","status":"Downloading"}"""),
        new("GET", "api/releases/{id:guid}/download", "kiosk", Target.Nothing),

        // Step 7B, M17 (T-321).
        new("POST", "api/kiosks/me/logs", "kiosk", Body: "{}"),
    ];

    [DockerFact]
    public async Task Each_role_reaches_exactly_the_endpoints_of_step_7A_its_permissions_name_and_no_others_kiosk()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix");

        var wrong = new List<string>();

        foreach (var call in AdminCalls)
        {
            foreach (var caller in callers.Admins)
            {
                var status = await SendAsync(caller.Client, call, callers);
                var allowed = caller.Permissions.Contains(call.Permission)
                              // An account of some kiosks has nothing of the whole service (T-311).
                              && !(caller.Scoped && Permissions.GlobalOnly.Contains(call.Permission))
                              // The Hangfire panel is a SuperAdmin's, whatever a role says (T-167).
                              && (call.Template != "api/admin/hangfire-session" || caller.Name == "SuperAdmin");

                var expected = !allowed
                    ? "403"
                    : caller.Scoped && NamesTheirKiosk(call) ? "404" : "allowed";

                if (Verdict(status, expected) is { } problem)
                {
                    wrong.Add($"{caller.Name,-14} {call.Endpoint}: {problem}");
                }
            }

            foreach (var (name, client, expected) in callers.Strangers)
            {
                var status = await SendAsync(client, call, callers);
                if (Verdict(status, expected) is { } problem)
                {
                    wrong.Add($"{name,-14} {call.Endpoint}: {problem}");
                }
            }
        }

        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));
    }

    [DockerFact]
    public async Task The_kiosk_endpoints_of_step_7A_are_a_kiosks_and_nobody_elses()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix-k");
        var superAdmin = callers.Admins.Single(caller => caller.Name == "SuperAdmin").Client;

        var wrong = new List<string>();

        foreach (var call in KioskCalls)
        {
            foreach (var (name, client, expected) in new[]
                     {
                         ("kiosk", callers.Theirs.Client, "allowed"),
                         ("SuperAdmin", superAdmin, "403"),
                         ("user", callers.User.Client, "403"),
                         ("anonymous", callers.Anonymous, "401"),
                     })
            {
                if (Verdict(await SendAsync(client, call, callers), expected) is { } problem)
                {
                    wrong.Add($"{name,-10} {call.Endpoint}: {problem}");
                }
            }
        }

        // The app user's own order: theirs, not a kiosk's or a panel's.
        var cancel = new Call("POST", "api/print-jobs/my/{id:guid}/cancel", "user", Target.Nothing);
        foreach (var (name, client, expected) in new[]
                 {
                     ("user", callers.User.Client, "allowed"),
                     ("kiosk", callers.Theirs.Client, "403"),
                     ("SuperAdmin", superAdmin, "403"),
                     ("anonymous", callers.Anonymous, "401"),
                 })
        {
            if (Verdict(await SendAsync(client, cancel, callers), expected) is { } problem)
            {
                wrong.Add($"{name,-10} {cancel.Endpoint}: {problem}");
            }
        }

        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));
    }

    [DockerFact]
    public async Task A_kiosk_cannot_answer_for_another_kiosks_command()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var mine = await ReadyKioskAsync(_fixture, "matrix-cmd-a");
        using var theirs = await ReadyKioskAsync(_fixture, "matrix-cmd-b");

        var sent = await admin.PostAsync($"/api/admin/kiosks/{mine.KioskId}/restart-app", null);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var commandId = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        async Task<string?> StatusAsync() => (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{mine.KioskId}/commands"))
            .EnumerateArray().Single(command => command.GetProperty("id").GetGuid() == commandId).GetProperty("status").GetString();
        var before = await StatusAsync();

        (await theirs.Client.PostAsync($"/api/kiosks/me/commands/{commandId}/ack", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a command is its own kiosk's");
        (await theirs.Client.PostAsJsonAsync($"/api/kiosks/me/commands/{commandId}/result", new { succeeded = true })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await StatusAsync()).Should().Be(before, "the other kiosk's answers changed nothing");
    }

    // -----------------------------------------------------------------------
    // Step 7A: another kiosk's data in the lists
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_finds_nothing_of_another_kiosk_in_the_lists_of_step_7A()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix-l");
        var superAdmin = callers.Admins.Single(caller => caller.Name == "SuperAdmin").Client;
        var scoped = callers.Admins.Single(caller => caller.Scoped).Client;
        var theirs = callers.Theirs;

        // Something of the other kiosk in every list.
        (await theirs.Client.PostAsJsonAsync("/api/incidents", new { type = "PaperJam" })).IsSuccessStatusCode.Should().BeTrue();
        (await SendAsync(superAdmin, AdminCalls.Single(call => call.Endpoint == "POST api/admin/supply-rules"), callers)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(superAdmin, AdminCalls.Single(call => call.Endpoint == "POST api/admin/service-tasks"), callers)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(superAdmin, AdminCalls.Single(call => call.Endpoint == "POST api/admin/inventory/movements"), callers)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(superAdmin, AdminCalls.Single(call => call.Endpoint == "POST api/admin/kiosks/{id:guid}/pause"), callers)).Should().Be(HttpStatusCode.OK);

        // The audit log is not the Admin role's; its scope is AdminAuditTests'.
        var lists = new[]
        {
            "api/admin/incidents", "api/admin/supply-rules", "api/admin/supply-alerts?active=", "api/admin/service-tasks",
            "api/admin/service-tasks/summary", "api/admin/inventory/movements", "api/admin/printers",
            "api/admin/operator/kiosks", "api/admin/analytics/pilot?" + LastWeek,
        };

        var holdsTheirs = new HashSet<string>(StringComparer.Ordinal)
        {
            "api/admin/incidents", "api/admin/supply-rules", "api/admin/service-tasks", "api/admin/inventory/movements",
            "api/admin/operator/kiosks",
        };

        var theirId = theirs.KioskId.ToString();
        var wrong = new List<string>();

        foreach (var list in lists)
        {
            // The account that sees every kiosk does see it - so the list is not simply empty.
            var everything = await superAdmin.GetAsync(list);
            everything.StatusCode.Should().Be(HttpStatusCode.OK, list);
            if (holdsTheirs.Contains(list))
            {
                (await everything.Content.ReadAsStringAsync()).Should().Contain(theirId, $"{list} holds something of the other kiosk");
            }

            var all = await scoped.GetAsync(list);
            var body = await all.Content.ReadAsStringAsync();
            if (all.StatusCode != HttpStatusCode.OK || body.Contains(theirId, StringComparison.OrdinalIgnoreCase) || body.Contains(theirs.KioskCode, StringComparison.Ordinal))
            {
                wrong.Add($"{list}: {(int)all.StatusCode}, names the other kiosk: {body.Contains(theirId, StringComparison.OrdinalIgnoreCase)}");
            }

            // Asked by name: not found, or nothing.
            var separator = list.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var asked = await scoped.GetAsync($"{list}{separator}kioskId={theirId}");
            var askedBody = await asked.Content.ReadAsStringAsync();
            if (asked.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.OK) || askedBody.Contains(theirId, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"{list}?kioskId=theirs: {(int)asked.StatusCode}");
            }
        }

        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));
    }

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_may_not_set_a_rule_for_every_kiosk_or_change_one_of_another_kiosk()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix-r");
        var superAdmin = callers.Admins.Single(caller => caller.Name == "SuperAdmin").Client;
        var scoped = callers.Admins.Single(caller => caller.Scoped).Client;

        // A rule for every kiosk, and one for the other kiosk: the SuperAdmin's.
        var global = await superAdmin.PostAsync("/api/admin/supply-rules", Json(Rule(callers.Theirs.KioskId.ToString()).Replace($"\"{callers.Theirs.KioskId}\"", string.Empty, StringComparison.Ordinal)));
        global.StatusCode.Should().Be(HttpStatusCode.OK, await global.Content.ReadAsStringAsync());
        var globalId = (await global.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var theirRule = await superAdmin.PostAsync("/api/admin/supply-rules", Json(Rule(callers.Theirs.KioskId.ToString())));
        theirRule.StatusCode.Should().Be(HttpStatusCode.OK, await theirRule.Content.ReadAsStringAsync());
        var theirRuleId = (await theirRule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var mineOnly = Rule(callers.Mine.KioskId.ToString());

        // A rule of its own kiosk is the scoped account's to make.
        (await scoped.PostAsync("/api/admin/supply-rules", Json(mineOnly))).StatusCode.Should().Be(HttpStatusCode.OK);

        // Every kiosk, or another one: not.
        var everyKiosk = Rule(callers.Mine.KioskId.ToString()).Replace($"\"{callers.Mine.KioskId}\"", string.Empty, StringComparison.Ordinal);
        var refused = await scoped.PostAsync("/api/admin/supply-rules", Json(everyKiosk));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("supplyRules.outsideScope", "the panel says what to do instead");
        (await scoped.PutAsync($"/api/admin/supply-rules/{globalId}", Json(mineOnly))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scoped.DeleteAsync($"/api/admin/supply-rules/{globalId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scoped.PutAsync($"/api/admin/supply-rules/{theirRuleId}", Json(mineOnly))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.DeleteAsync($"/api/admin/supply-rules/{theirRuleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rules = await superAdmin.GetFromJsonAsync<JsonElement>("/api/admin/supply-rules");
        rules.EnumerateArray().Select(rule => rule.GetProperty("id").GetGuid()).Should().Contain([globalId, theirRuleId], "nothing was changed or removed");
    }

    // -----------------------------------------------------------------------
    // The whole service: none of it for an account of some kiosks (T-311)
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_has_nothing_of_the_whole_service_whatever_its_role_holds()
    {
        if (Skip) { return; }

        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        using var mine = await ReadyKioskAsync(_fixture, "matrix-global");

        // One role with every permission there is: given to an account of one kiosk and to one of every kiosk.
        var roleName = $"Everything {Guid.NewGuid():N}";
        (await superAdmin.PostAsJsonAsync("/api/admin/roles", new { name = roleName, description = "Matrix", permissions = Permissions.All }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var scoped = await AccountAsync("one kiosk", roleName, KioskScope.Only(mine.KioskId));
        var global = await AccountAsync("every kiosk", roleName, KioskScope.Everything());

        // What the panel is told it may do.
        var me = await scoped.Client.GetFromJsonAsync<JsonElement>("/api/admin/me");
        var granted = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        granted.Should().NotIntersectWith(Permissions.GlobalOnly, "the panel offers none of the whole service");
        granted.Should().Contain([Permissions.InventoryView, Permissions.OperatorPanel], "the warehouse is still seen and taken from");

        var tried = new List<string>();
        var wrong = new List<string>();

        foreach (var endpoint in Endpoints())
        {
            var needs = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().OfType<HasPermissionAttribute>().Select(a => a.Permission).ToList();
            if (needs.Count == 0 || !needs.All(Permissions.GlobalOnly.Contains))
            {
                continue;
            }

            var template = endpoint.RoutePattern.RawText!.TrimStart('/');
            var path = "/" + System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}:]+(:[^}]+)?\}", match =>
                match.Value.Contains("regex", StringComparison.Ordinal) ? "pause" : Guid.NewGuid().ToString());

            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                async Task<int> SendAs(HttpClient client)
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), path);
                    request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
                    if (method is "POST" or "PUT")
                    {
                        request.Content = Json("{}");
                    }

                    using var response = await client.SendAsync(request);
                    return (int)response.StatusCode;
                }

                var asScoped = await SendAs(scoped.Client);
                var asGlobal = method == "GET" ? await SendAs(global.Client) : 0;
                tried.Add($"{method} {template} [{string.Join(", ", needs)}]: one kiosk {asScoped}{(method == "GET" ? $", every kiosk {asGlobal}" : string.Empty)}");

                if (asScoped != 403 || asGlobal is 401 or 403)
                {
                    wrong.Add(tried[^1]);
                }
            }
        }

        // The warehouse stays: what an operator takes to the kiosk.
        (await scoped.Client.GetAsync("/api/admin/inventory/items")).StatusCode.Should().Be(HttpStatusCode.OK);

        Console.WriteLine(string.Join(Environment.NewLine, tried));
        tried.Should().HaveCountGreaterThan(40, "every endpoint of the whole service is tried");
        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));
    }

    // -----------------------------------------------------------------------
    // Every kiosk-bound record: another kiosk's (T-301)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The records that belong to a kiosk, by the route parameter that names
    /// them. Everything an admin endpoint takes by id and that is a kiosk's is
    /// here; what is not here belongs to no kiosk (roles, admins, users and
    /// wallets, promos, prices, storage, enrollment tokens, warehouse items).
    /// </summary>
    private static readonly string[] KioskBound =
    [
        "api/admin/kiosks/", "api/admin/operator/kiosks/", "api/admin/payments/", "api/admin/print-jobs/",
        "api/admin/refunds/", "api/admin/fiscal-receipts/", "api/admin/receipts/", "api/admin/incidents/",
        "api/admin/service-tasks/", "api/admin/devices/", "api/admin/support/claims/", "api/admin/supply-rules/",
    ];

    [DockerFact]
    public async Task An_account_that_looks_after_one_kiosk_can_neither_read_nor_change_nor_remove_anything_of_another_kiosk()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix-x");
        var superAdmin = callers.Admins.Single(caller => caller.Name == "SuperAdmin").Client;
        var scoped = callers.Admins.Single(caller => caller.Scoped).Client;
        var theirs = callers.Theirs;

        // One of everything, at the other kiosk.
        var their = await TheirRecordsAsync(superAdmin, theirs);

        async Task<string?> StatusAsync(string path)
        {
            var body = await superAdmin.GetFromJsonAsync<JsonElement>(path);
            return (body.TryGetProperty("incident", out var incident) ? incident : body).GetProperty("status").GetString();
        }
        var taskBefore = await StatusAsync($"/api/admin/service-tasks/{their["task"]}");
        var incidentBefore = await StatusAsync($"/api/admin/incidents/{their["incident"]}");

        string? IdFor(string template)
        {
            var path = template.TrimStart('/');
            if (path.StartsWith("api/admin/kiosks/{kioskId:guid}", StringComparison.Ordinal)) { return their["kiosk"]; }

            return KioskBound.FirstOrDefault(prefix => path.StartsWith(prefix, StringComparison.Ordinal)) switch
            {
                "api/admin/kiosks/" or "api/admin/operator/kiosks/" => their["kiosk"],
                "api/admin/payments/" => their["payment"],
                "api/admin/print-jobs/" => their["job"],
                "api/admin/refunds/" => their["refund"],
                "api/admin/fiscal-receipts/" => their["fiscal"],
                "api/admin/receipts/" => their["receipt"],
                "api/admin/incidents/" => their["incident"],
                "api/admin/service-tasks/" => their["task"],
                "api/admin/devices/" => their["device"],
                "api/admin/support/claims/" => their["claim"],
                "api/admin/supply-rules/" => their["rule"],
                _ => null,
            };
        }

        var tried = new List<string>();
        var wrong = new List<string>();

        foreach (var endpoint in Endpoints())
        {
            var template = endpoint.RoutePattern.RawText!.TrimStart('/');
            if (!template.Contains('{', StringComparison.Ordinal) || IdFor(template) is not { } id)
            {
                continue;
            }

            // A second parameter is the action of a storage job and the like; none of those is a kiosk's.
            var path = "/" + System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", id);
            foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
                if (method is "POST" or "PUT")
                {
                    request.Content = Json("{}");
                }

                using var response = await scoped.SendAsync(request);
                var code = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync();
                tried.Add($"{method} {template}: {code}");

                // Not found or not allowed. A refused body (400) changes nothing either; it is
                // listed so the report shows which endpoints check the body first.
                if (code is not (403 or 404 or 400) || body.Contains(theirs.KioskCode, StringComparison.Ordinal))
                {
                    wrong.Add($"{method} {template}: {code} {body[..Math.Min(body.Length, 160)]}");
                }
            }
        }

        // The lists: nothing of the other kiosk, unfiltered or asked for by name.
        foreach (var list in new[]
                 {
                     "api/admin/payments", "api/admin/refunds", "api/admin/fiscal-receipts", "api/admin/receipts",
                     "api/admin/print-jobs", "api/admin/print-jobs/suspected", "api/admin/incidents", "api/admin/service-tasks",
                     "api/admin/devices", "api/admin/support/claims", "api/admin/supply-rules", "api/admin/supply-alerts?active=",
                     "api/admin/inventory/movements", "api/admin/kiosks", "api/admin/printers", "api/admin/operator/kiosks",
                     "api/admin/dashboard/payments", $"api/admin/reports/revenue?{LastWeek}", $"api/admin/analytics/pilot?{LastWeek}",
                 })
        {
            foreach (var asked in new[] { list, $"{list}{(list.Contains('?', StringComparison.Ordinal) ? "&" : "?")}kioskId={theirs.KioskId}" })
            {
                using var response = await scoped.GetAsync(asked);
                var body = await response.Content.ReadAsStringAsync();
                tried.Add($"GET {asked.Replace(theirs.KioskId.ToString(), "{theirs}", StringComparison.Ordinal)}: {(int)response.StatusCode}");

                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NotFound)
                    || their.Values.Any(value => body.Contains(value, StringComparison.OrdinalIgnoreCase))
                    || body.Contains(theirs.KioskCode, StringComparison.Ordinal))
                {
                    wrong.Add($"GET {asked}: {(int)response.StatusCode}, names the other kiosk's records");
                }
            }
        }

        // Where the body is checked before the kiosk (400 above), a body that would pass:
        // still not found, so the scope holds and not only the validation.
        var kioskAsItIs = await superAdmin.GetStringAsync($"/api/admin/kiosks/{theirs.KioskId}");
        foreach (var (method, path, body) in new (string, string, string)[]
                 {
                     ("PUT", $"/api/admin/kiosks/{theirs.KioskId}", kioskAsItIs),
                     ("POST", $"/api/admin/kiosks/{theirs.KioskId}/admin-pin", """{"pin":"2468"}"""),
                     ("POST", $"/api/admin/devices/{theirs.DeviceId}/activate", $$"""{"kioskId":"{{callers.Mine.KioskId}}"}"""),
                     ("POST", $"/api/admin/incidents/{their["incident"]}/resolve", """{"note":"matrix"}"""),
                     ("POST", $"/api/admin/print-jobs/{their["job"]}/cancel", """{"reason":"matrix"}"""),
                     ("POST", $"/api/admin/print-jobs/{their["job"]}/resolve", """{"decision":"NoRefund","reason":"matrix","note":"matrix"}"""),
                     ("POST", $"/api/admin/service-tasks/{their["task"]}/cancel", """{"reason":"matrix"}"""),
                     ("PUT", $"/api/admin/service-tasks/{their["task"]}", """{"priority":"Low"}"""),
                     ("PUT", $"/api/admin/supply-rules/{their["rule"]}", Rule(callers.Mine.KioskId.ToString())),
                     ("POST", $"/api/admin/kiosks/{theirs.KioskId}/paper/refill", """{"paper":"A4","mode":"Add","sheets":1}"""),
                 })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = Json(body) };
            request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
            using var response = await scoped.SendAsync(request);
            var template = path.Replace(theirs.KioskId.ToString(), "{theirs}", StringComparison.Ordinal);
            tried.Add($"{method} {template} (a body that would pass): {(int)response.StatusCode}");

            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden))
            {
                wrong.Add($"{method} {template} with a body that would pass: {(int)response.StatusCode} {(await response.Content.ReadAsStringAsync())[..Math.Min(160, (int)(response.Content.Headers.ContentLength ?? 160))]}");
            }
        }

        // What was tried, for the report (T-301).
        Console.WriteLine(string.Join(Environment.NewLine, tried));

        wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));

        // Nothing of theirs was changed or removed: the SuperAdmin still reads it all as it was.
        (await superAdmin.GetAsync($"/api/admin/kiosks/{theirs.KioskId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusAsync($"/api/admin/service-tasks/{their["task"]}")).Should().Be(taskBefore);
        (await StatusAsync($"/api/admin/incidents/{their["incident"]}")).Should().Be(incidentBefore);
        (await superAdmin.GetFromJsonAsync<JsonElement>("/api/admin/supply-rules")).EnumerateArray()
            .Should().Contain(rule => rule.GetProperty("id").GetString() == their["rule"]);
    }

    /// <summary>A paid guest order with its payment, fiscal receipt and receipt, a refund, an incident, a task, a claim and a rule - all of one kiosk.</summary>
    private async Task<Dictionary<string, string>> TheirRecordsAsync(HttpClient superAdmin, TestKiosk theirs)
    {
        var paid = await PaidBankJobAsync(_fixture, theirs);
        string jobCode;
        using (var lookup = _fixture.Services.CreateScope())
        {
            jobCode = (await lookup.ServiceProvider.GetRequiredService<MongoContext>().PrintJobs.Find(j => j.Id == paid.JobId).FirstAsync()).JobCode;
        }

        (await theirs.Client.PostAsJsonAsync("/api/incidents", new { type = "PaperJam" })).EnsureSuccessStatusCode();

        var task = await superAdmin.PostAsync("/api/admin/service-tasks", Json($$"""{"kioskId":"{{theirs.KioskId}}","type":"Cleaning"}"""));
        task.StatusCode.Should().Be(HttpStatusCode.OK, await task.Content.ReadAsStringAsync());

        var rule = await superAdmin.PostAsync("/api/admin/supply-rules", Json(Rule(theirs.KioskId.ToString())));
        rule.StatusCode.Should().Be(HttpStatusCode.OK, await rule.Content.ReadAsStringAsync());

        using (var anonymous = _fixture.CreateApiClient())
        {
            using var claim = new HttpRequestMessage(HttpMethod.Post, "/api/support/claims")
            {
                Content = JsonContent.Create(new { jobCode, kioskCode = theirs.KioskCode, text = "Çap çıxmadı" }),
            };
            claim.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
            (await anonymous.SendAsync(claim)).EnsureSuccessStatusCode();
        }

        // The incident above paused the kiosk: the guest's waiting bank payment is refunded by itself (plan 9.6).

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var fiscal = await EventuallyAsync(
            () => context.FiscalReceipts.Find(r => r.PaymentId == paid.PaymentId).FirstOrDefaultAsync(), r => r is not null, "the fiscal receipt");
        var receipt = await EventuallyAsync(
            () => context.Receipts.Find(r => r.PaymentId == paid.PaymentId).FirstOrDefaultAsync(), r => r is not null, "the receipt");
        var refundRow = await EventuallyAsync(
            () => context.Refunds.Find(r => r.PaymentId == paid.PaymentId).FirstOrDefaultAsync(), r => r is not null, "the automatic refund");
        var incident = await context.Incidents.Find(i => i.KioskId == theirs.KioskId && i.Status == Domain.Incidents.IncidentStatus.Open).FirstAsync();
        var claimRow = await context.SupportClaims.Find(c => c.KioskId == theirs.KioskId).FirstAsync();

        return new Dictionary<string, string>
        {
            ["kiosk"] = theirs.KioskId.ToString(),
            ["device"] = theirs.DeviceId.ToString(),
            ["payment"] = paid.PaymentId.ToString(),
            ["job"] = paid.JobId.ToString(),
            ["refund"] = refundRow!.Id.ToString(),
            ["fiscal"] = fiscal!.Id.ToString(),
            ["receipt"] = receipt!.Id.ToString(),
            ["incident"] = incident.Id.ToString(),
            ["task"] = (await task.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!,
            ["claim"] = claimRow.Id.ToString(),
            ["rule"] = (await rule.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!,
        };
    }

    // -----------------------------------------------------------------------
    // Step 7A: role x panel event
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Each_panel_event_of_step_7A_reaches_the_roles_that_may_read_it_and_nobody_else()
    {
        if (Skip) { return; }

        var callers = await CallersAsync("matrix-h");
        var kioskId = callers.Theirs.KioskId;

        var panels = new List<(Caller Caller, RawHubConnection Hub)>();
        try
        {
            foreach (var caller in callers.Admins)
            {
                panels.Add((caller, await RawHubConnection.ConnectAsync(_fixture, "admin", caller.Token)));
            }

            // The hub is for an admin token.
            foreach (var token in new[] { callers.User.Client.DefaultRequestHeaders.Authorization!.Parameter!, callers.Theirs.AccessToken })
            {
                using var client = _fixture.CreateApiClient();
                var negotiate = await client.PostAsync($"/hubs/admin/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);
                negotiate.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
            }

            using (var scope = _fixture.Services.CreateScope())
            {
                var notifier = scope.ServiceProvider.GetRequiredService<IAdminNotifier>();
                var payments = scope.ServiceProvider.GetRequiredService<IPaymentsAdminNotifier>();

                await notifier.KioskStatusChangedAsync(kioskId, "Offline");
                await notifier.KioskTelemetryUpdatedAsync(Make<KioskTelemetryView>(new { kioskId }));
                await notifier.PrinterTelemetryUpdatedAsync(kioskId, []);
                await notifier.KioskPrintersUpdatedAsync(kioskId);
                await notifier.CommandResultAsync(kioskId, Make<KioskCommandView>(new { id = Guid.NewGuid(), kioskId }));
                await notifier.SupplyAlertRaisedAsync(Make<SupplyAlertView>(new { id = Guid.NewGuid(), kioskId }));
                await notifier.SupplyAlertClearedAsync(Make<SupplyAlertView>(new { id = Guid.NewGuid(), kioskId }));
                await notifier.ServiceTaskAssignedAsync(Make<ServiceTaskView>(new { id = Guid.NewGuid(), kioskId }));
                await notifier.ServiceTaskUpdatedAsync(Make<ServiceTaskView>(new { id = Guid.NewGuid(), kioskId }));
                await payments.IncidentRaisedAsync(Guid.NewGuid(), "PaperJam", "Warning", kioskId: kioskId);
                await payments.IncidentResolvedAsync(Guid.NewGuid(), "PaperJam", "Resolved", kioskId: kioskId);

                // Money, orders and machines of the kiosk (T-301).
                await notifier.QueueUpdatedAsync(kioskId, []);
                await notifier.DeviceActivatedAsync(Guid.NewGuid(), kioskId);
                await notifier.DeviceRevokedAsync(Guid.NewGuid(), kioskId);
                await payments.PaymentSucceededAsync(Guid.NewGuid(), kioskId, 1m);
                await payments.RefundStatusChangedAsync(Guid.NewGuid(), Guid.NewGuid(), "Succeeded", kioskId: kioskId);
                await payments.FiscalStatusChangedAsync(Guid.NewGuid(), "Issued", kioskId: kioskId);

                // Releases and the kiosks' versions: the whole service's (T-317).
                await notifier.ReleasesChangedAsync();
                await notifier.KioskUpdateReportedAsync(Make<Application.Releases.KioskVersionView>(new { kioskId }));

                // Everybody hears this one: the end of what the test sent.
                await notifier.PermissionsChangedAsync(Guid.Empty);
            }

            // Who may hear each: any one of these permissions - what the page behind it asks for.
            var audienceOf = new Dictionary<string, string[]>
            {
                ["KioskStatusChanged"] = [Permissions.KiosksView],
                ["KioskTelemetryUpdated"] = [Permissions.KiosksView],
                ["CommandResult"] = [Permissions.KiosksView],
                ["PrinterTelemetryUpdated"] = [Permissions.PrintersView],
                ["KioskPrintersUpdated"] = [Permissions.PrintersView],
                ["SupplyAlertRaised"] = [Permissions.SuppliesView],
                ["SupplyAlertCleared"] = [Permissions.SuppliesView],
                ["ServiceTaskAssigned"] = [Permissions.ServiceTasksView],
                ["ServiceTaskUpdated"] = [Permissions.ServiceTasksView],
                ["IncidentRaised"] = [Permissions.IncidentsView],
                ["IncidentResolved"] = [Permissions.IncidentsView],
                ["QueueUpdated"] = [Permissions.JobsView],
                ["DeviceActivated"] = [Permissions.DevicesView],
                ["DeviceRevoked"] = [Permissions.DevicesView],
                ["PaymentSucceeded"] = [Permissions.PaymentsView],
                ["RefundStatusChanged"] = [Permissions.RefundsView, Permissions.PaymentsView],
                ["FiscalStatusChanged"] = [Permissions.FiscalView, Permissions.ReceiptsView, Permissions.PaymentsView],
                ["ReleasesChanged"] = [Permissions.ReleasesView],
                ["KioskUpdateReported"] = [Permissions.ReleasesView],
            };

            var wrong = new List<string>();

            foreach (var (caller, hub) in panels)
            {
                // The end marker goes to another group; over the Redis backplane it
                // may overtake what was sent before it, so a little longer is listened.
                var heard = (await hub.ReadUntilAsync(
                        (target, argument) => target == "PermissionsChanged" && argument.GetProperty("roleId").GetGuid() == Guid.Empty,
                        TimeSpan.FromSeconds(15)))
                    .Concat(await hub.ReadForAsync(TimeSpan.FromSeconds(3)))
                    .Select(item => item.Target)
                    .Where(audienceOf.ContainsKey)
                    .ToHashSet();

                var expected = caller.Scoped
                    ? []
                    : audienceOf.Where(pair => pair.Value.Any(caller.Permissions.Contains)).Select(pair => pair.Key).ToHashSet();

                if (!heard.SetEquals(expected))
                {
                    wrong.Add($"{caller.Name}: heard [{string.Join(", ", heard.Order())}], expected [{string.Join(", ", expected.Order())}]");
                }
            }

            wrong.Should().BeEmpty(string.Join(Environment.NewLine, wrong));
        }
        finally
        {
            foreach (var (_, hub) in panels)
            {
                await hub.DisposeAsync();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Test support
    // -----------------------------------------------------------------------

    private sealed record Caller(string Name, HttpClient Client, string Token, IReadOnlySet<string> Permissions, bool Scoped);

    private sealed record Callers(
        IReadOnlyList<Caller> Admins,
        TestKiosk Mine,
        TestKiosk Theirs,
        TestUser User,
        HttpClient Anonymous,
        Guid ItemId)
    {
        /// <summary>Who holds no admin token: never allowed into the panel's endpoints.</summary>
        public IEnumerable<(string Name, HttpClient Client, string Expected)> Strangers =>
        [
            ("anonymous", Anonymous, "401"),
            ("user", User.Client, "403"),
            ("kiosk", Theirs.Client, "403"),
        ];
    }

    /// <summary>
    /// The seeded SuperAdmin; an account for each of the other seeded roles
    /// (Admin, Operator, Maliyyə), seeing every kiosk; an Admin that looks
    /// after one kiosk only; an app user; two kiosks - the scoped account's
    /// and another; and nobody.
    /// </summary>
    private async Task<Callers> CallersAsync(string name)
    {
        var mine = await ReadyKioskAsync(_fixture, $"{name}-mine");
        var theirs = await ReadyKioskAsync(_fixture, $"{name}-theirs");

        var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var admins = new List<Caller>
        {
            new("SuperAdmin", superAdmin, superAdmin.DefaultRequestHeaders.Authorization!.Parameter!, Permissions.All, Scoped: false),
        };

        foreach (var role in new[] { "Admin", "Operator", "Maliyyə" })
        {
            admins.Add(await AccountAsync(role, role, KioskScope.Everything()));
        }

        admins.Add(await AccountAsync("Admin of one", "Admin", KioskScope.Only(mine.KioskId)));

        var item = await superAdmin.PostAsJsonAsync("/api/admin/inventory/items", new
        {
            sku = $"MX-{Guid.NewGuid():N}"[..20],
            name = "Matrix paper",
            type = "PaperA4Ream",
            unit = "bağlama",
            sheetsPerUnit = 500,
            minStock = 0,
            isActive = true,
        });
        item.StatusCode.Should().Be(HttpStatusCode.OK, await item.Content.ReadAsStringAsync());

        return new Callers(
            admins,
            mine,
            theirs,
            await TestUsers.ConfirmedAsync(_fixture),
            _fixture.CreateApiClient(),
            (await item.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    private async Task<Caller> AccountAsync(string name, string roleName, KioskScope kioskScope)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

        var role = await context.AdminRoles.Find(r => r.Name == roleName).SingleAsync();

        var account = new AdminUser
        {
            Email = $"matrix.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Matrix " + name,
            RoleIds = [role.Id],
            KioskScope = kioskScope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, Password);
        await context.AdminUsers.InsertOneAsync(account);

        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return new Caller(name, client, token, role.Permissions.ToHashSet(StringComparer.Ordinal), Scoped: !kioskScope.All);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, Call call, Callers callers)
    {
        var id = call.Target switch
        {
            Target.TheirKiosk => callers.Theirs.KioskId,
            _ => Guid.NewGuid(),
        };

        string Fill(string text) => text
            .Replace("{kiosk}", callers.Theirs.KioskId.ToString(), StringComparison.Ordinal)
            .Replace("{item}", callers.ItemId.ToString(), StringComparison.Ordinal);

        var path = "/" + call.Template.Replace("{id:guid}", id.ToString(), StringComparison.Ordinal)
            .Replace("{logId:guid}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        if (call.Query is not null)
        {
            path += "?" + Fill(call.Query);
        }

        using var request = new HttpRequestMessage(new HttpMethod(call.Method), path);
        request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
        if (call.Body is not null)
        {
            request.Content = Json(Fill(call.Body));
        }

        using var response = await client.SendAsync(request);

        return response.StatusCode;
    }

    /// <summary>Null when the status is what was expected: "allowed" is anything but 401, 403 and a server error.</summary>
    private static string? Verdict(HttpStatusCode status, string expected)
    {
        var code = (int)status;
        var ok = expected switch
        {
            "allowed" => code is not (401 or 403) && code < 500,
            _ => code.ToString(System.Globalization.CultureInfo.InvariantCulture) == expected,
        };

        return ok ? null : $"{code}, expected {expected}";
    }


    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    private static T Make<T>(object properties) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(properties, Web), Web)!;

    /// <summary>
    /// A caller who may not call an endpoint learns nothing of it - not even
    /// what body it takes (T-318). An action with a file parameter used to
    /// answer a wrong content type with 415 while choosing the route, before
    /// anybody was asked who they were; now every protected endpoint, whatever
    /// the body, says 401 first, and a signed-in caller's wrong body is a 400.
    /// </summary>
    [DockerFact]
    public async Task Nobody_learns_what_body_an_endpoint_takes_before_saying_who_they_are()
    {
        if (Skip) { return; }

        var anonymous = _fixture.CreateApiClient();
        var told = new List<string>();
        var tried = 0;

        foreach (var endpoint in Endpoints())
        {
            if (AnonymousOnPurpose.Contains(Name(endpoint)))
            {
                continue;
            }

            var path = "/" + System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText!.TrimStart('/'), @"\{[^}:]+(:[^}]+)?\}", match =>
                match.Value.Contains("regex", StringComparison.Ordinal) ? "pause" : Guid.NewGuid().ToString());

            foreach (var method in (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Where(m => m is "POST" or "PUT" or "PATCH"))
            {
                foreach (var body in new HttpContent[] { new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), new StringContent("x", System.Text.Encoding.UTF8, "text/plain") })
                {
                    using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
                    request.Headers.Add(IdempotencyFilter.HeaderName, Guid.NewGuid().ToString());
                    using var response = await anonymous.SendAsync(request);
                    tried++;

                    if (response.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        told.Add($"{method} {path} ({body.Headers.ContentType!.MediaType}): {(int)response.StatusCode}");
                    }
                }
            }
        }

        tried.Should().BeGreaterThan(100);
        told.Should().BeEmpty("a caller who has not said who they are gets 401 and nothing else");

        // Signed in, the wrong body is the caller's mistake: 400, never a 415 that skipped a check.
        var user = await TestUsers.ConfirmedAsync(_fixture);
        var asUser = await user.Client.PostAsJsonAsync("/api/files", new { });
        asUser.StatusCode.Should().Be(HttpStatusCode.BadRequest, await asUser.Content.ReadAsStringAsync());

        using var kiosk = await ReadyKioskAsync(_fixture, "matrix-form");
        var asKiosk = await kiosk.Client.PostAsJsonAsync($"/api/print-jobs/{Guid.NewGuid()}/flash-upload", new { });
        asKiosk.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    private IEnumerable<RouteEndpoint> Endpoints()
        => _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is { } raw
                               && raw.TrimStart('/').StartsWith("api/", StringComparison.Ordinal));

    private static string Name(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];

        return $"{string.Join(",", methods)} {endpoint.RoutePattern.RawText!.TrimStart('/')}";
    }
}
