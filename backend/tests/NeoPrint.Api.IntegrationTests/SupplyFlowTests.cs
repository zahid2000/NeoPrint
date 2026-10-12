using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The supply module (plan sections 2.15, 9.9): a tray running low raises an
/// alert and a task for the kiosk's operator, the operator refills and
/// finishes it, the counter and the warehouse follow and the alert clears.
/// </summary>
/// <remarks>
/// The test host does not run the supply jobs by itself: a test runs the
/// evaluator, the forecast and the escalation when it means to. What follows a
/// paper movement by its event (<c>SupplyLevelChanged</c>) is waited for.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class SupplyFlowTests
{
    private const string OperatorPassword = "SupplyOperator!2026";

    private static readonly string[] OperatorPermissions =
    [
        Permissions.KiosksView, Permissions.SuppliesView, Permissions.PaperRefill,
        Permissions.ServiceTasksView, Permissions.ServiceTasksComplete, Permissions.InventoryView,
    ];

    private readonly NeoPrintApiFixture _fixture;

    public SupplyFlowTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // Plan section 9.9, start to finish
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Paper_running_low_alerts_gives_the_operator_a_task_and_a_refill_clears_it_all()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "supply-flow");
        var (operatorAccount, operatorClient) = await OperatorAsync(KioskScope.Only(kiosk.KioskId));
        using var _client = operatorClient;
        await using var _others = await OnlyTheseOperatorsAsync(operatorAccount.Id);
        await using var panel = await RawHubConnection.ConnectAsync(_fixture, "admin", Token(operatorClient));
        var reams = await ItemAsync(admin, InventoryItemType.PaperA4Ream, stock: 10, minStock: 2, sheetsPerUnit: 500);

        (await RefillAsync(admin, kiosk.KioskId, "Exact", 205)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 1-2: a job takes the tray below 200.
        await PrintAsync(kiosk, sheets: 10);

        var warning = await EventuallyAsync(
            () => ActiveAlertsAsync(kiosk.KioskId), alerts => alerts.Count == 1, "the Warning alert after the job's sheets");
        warning[0].Level.Should().Be(SupplyAlertLevel.Warning);
        warning[0].Resource.Should().Be(SupplyResource.PaperA4);
        warning[0].Value.Should().Be(195);

        var task = await TaskAsync(warning[0].ServiceTaskId!.Value);
        task.Type.Should().Be(ServiceTaskType.RefillPaper);
        task.Source.Should().Be(ServiceTaskSource.AlertRule);
        task.Status.Should().Be(ServiceTaskStatus.Assigned);
        task.AssignedTo.Should().Be(operatorAccount.Id, "the kiosk's operator");
        task.Priority.Should().Be(TaskPriority.High, "the seeded rule's priority");

        // The operator's open panel hears both, and gets an e-mail about the task.
        var heard = await panel.ReadUntilAsync((target, _) => target == "ServiceTaskAssigned", TimeSpan.FromSeconds(15));
        heard.Should().Contain(item => item.Target == "SupplyAlertRaised" || item.Target == "ServiceTaskAssigned");
        heard[^1].Argument.GetProperty("assignedTo").GetGuid().Should().Be(operatorAccount.Id);

        var mail = _fixture.Services.GetRequiredService<CapturedEmails>().Latest(operatorAccount.Email);
        mail.Should().NotBeNull();
        mail!.Subject.Should().Contain("Kağız doldurmaq").And.Contain(kiosk.KioskCode);

        // Looking again changes nothing: one alert, one task.
        (await EvaluateAsync(kiosk.KioskId)).Should().Be(SupplyEvaluationResult.Nothing);
        (await ActiveAlertsAsync(kiosk.KioskId)).Should().ContainSingle();

        // Step 3: below 50 it is Critical, and so is the task.
        (await RefillAsync(admin, kiosk.KioskId, "Exact", 55)).StatusCode.Should().Be(HttpStatusCode.OK);
        await PrintAsync(kiosk, sheets: 10);

        var critical = await EventuallyAsync(
            () => ActiveAlertsAsync(kiosk.KioskId), alerts => alerts.Count == 1 && alerts[0].Level == SupplyAlertLevel.Critical, "the alert becoming Critical");
        critical[0].Id.Should().Be(warning[0].Id, "the same alert, another level");
        var urgent = await TaskAsync(task.Id);
        urgent.Priority.Should().Be(TaskPriority.Critical);
        NeoPrint.Application.Localization.Messages.WriteStored(urgent.Notes, "az").Should().Contain("45", "the note follows the level");

        // Step 4: the operator, from their phone.
        var mine = await operatorClient.GetFromJsonAsync<JsonElement>("/api/admin/service-tasks?mine=true&status=Assigned");
        mine.GetProperty("items").EnumerateArray().Should().ContainSingle(item => item.GetProperty("id").GetGuid() == task.Id);

        (await operatorClient.PostAsync($"/api/admin/service-tasks/{task.Id}/start", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var done = await CompleteAsync(operatorClient, task.Id, new { refillMode = "Full", inventoryItemId = reams, quantity = 1, note = "Bir bağlama" });
        done.StatusCode.Should().Be(HttpStatusCode.OK, await done.Content.ReadAsStringAsync());

        var finished = await done.Content.ReadFromJsonAsync<JsonElement>();
        finished.GetProperty("status").GetString().Should().Be("Done");
        finished.GetProperty("completion").GetProperty("sheetsAdded").GetInt32().Should().Be(455);

        // Step 5: the counter is at its capacity, a ream has left the warehouse, the alert clears by itself.
        (await KioskAsync(kiosk.KioskId)).PaperStock["A4"].Current.Should().Be(500);

        var item = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/inventory/items")).EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == reams);
        item.GetProperty("stock").GetDecimal().Should().Be(9);

        var movements = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/inventory/movements?itemId={reams}");
        var used = movements.GetProperty("items").EnumerateArray().First();
        used.GetProperty("type").GetString().Should().Be("UsedAtKiosk");
        used.GetProperty("quantity").GetDecimal().Should().Be(-1);
        used.GetProperty("taskId").GetGuid().Should().Be(task.Id);
        used.GetProperty("kioskId").GetGuid().Should().Be(kiosk.KioskId);

        await EventuallyAsync(() => ActiveAlertsAsync(kiosk.KioskId), alerts => alerts.Count == 0, "the alert clearing after the refill");

        var cleared = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/supply-alerts?active=false&kioskId={kiosk.KioskId}");
        cleared.GetProperty("items").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("clearedAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        // Finishing it twice is refused: the tray is not filled a second time.
        (await CompleteAsync(operatorClient, task.Id, new { refillMode = "Full" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task A_level_that_recovers_without_the_task_clears_the_alert_and_the_task_nobody_started()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "supply-recover");

        (await RefillAsync(admin, kiosk.KioskId, "Exact", 120)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EvaluateAsync(kiosk.KioskId)).Raised.Should().BeLessThanOrEqualTo(1);

        var alert = (await ActiveAlertsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        var taskId = alert.ServiceTaskId!.Value;

        // An administrator fills the tray from the panel; nobody touched the task.
        (await RefillAsync(admin, kiosk.KioskId, "Full", 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        await EvaluateAsync(kiosk.KioskId);

        (await ActiveAlertsAsync(kiosk.KioskId)).Should().BeEmpty();

        var task = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/service-tasks/{taskId}");
        task.GetProperty("status").GetString().Should().Be("Cancelled");
        task.GetProperty("cancelReason").GetString().Should().Contain("həddən yuxarı", "the stored reason is written for the reader");
    }

    // -----------------------------------------------------------------------
    // Rules
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_rule_for_a_kiosk_replaces_the_global_one_there_and_nowhere_else()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var special = await ReadyKioskAsync(_fixture, "supply-rule-own");
        using var ordinary = await ReadyKioskAsync(_fixture, "supply-rule-global");

        (await RefillAsync(admin, special.KioskId, "Exact", 150)).EnsureSuccessStatusCode();
        (await RefillAsync(admin, ordinary.KioskId, "Exact", 150)).EnsureSuccessStatusCode();

        // A small kiosk: 150 sheets is plenty there.
        var created = await SendAsync(admin, HttpMethod.Post, "/api/admin/supply-rules", Rule("Kiçik kiosk", "PaperA4", "Sheets", 100, 20, kioskIds: [special.KioskId]));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var ruleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        try
        {
            await EvaluateAsync(special.KioskId);
            await EvaluateAsync(ordinary.KioskId);

            (await ActiveAlertsAsync(special.KioskId)).Should().BeEmpty("its own rule warns below 100");
            (await ActiveAlertsAsync(ordinary.KioskId)).Should().ContainSingle("the global rule warns below 200");

            // Below its own threshold it alerts too, by its own rule.
            (await RefillAsync(admin, special.KioskId, "Exact", 90)).EnsureSuccessStatusCode();
            await EvaluateAsync(special.KioskId);

            (await ActiveAlertsAsync(special.KioskId)).Should().ContainSingle().Which.RuleId.Should().Be(ruleId);
        }
        finally
        {
            (await admin.DeleteAsync($"/api/admin/supply-rules/{ruleId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Its rule gone, the kiosk is judged by the global one again: 90 is below 200 - still an alert, by that rule.
        var after = (await ActiveAlertsAsync(special.KioskId)).Should().ContainSingle().Subject;
        after.Level.Should().Be(SupplyAlertLevel.Warning);

        var audits = await AuditActionsAsync(ruleId);
        audits.Should().Contain(["supplyRules.create", "supplyRules.delete"]);
    }

    [DockerFact]
    public async Task A_rule_that_could_never_fire_is_refused_and_only_those_with_the_right_may_write_one()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var (_, operatorClient) = await OperatorAsync(KioskScope.Everything());
        using var _client = operatorClient;

        async Task<HttpStatusCode> CreateAsync(object rule) => (await SendAsync(admin, HttpMethod.Post, "/api/admin/supply-rules", rule)).StatusCode;

        (await CreateAsync(Rule("Tərsinə", "PaperA4", "Sheets", 50, 200))).Should().Be(HttpStatusCode.BadRequest, "the warning threshold is below the critical one");
        (await CreateAsync(Rule(" ", "PaperA4", "Sheets", 200, 50))).Should().Be(HttpStatusCode.BadRequest);
        (await CreateAsync(Rule("Toner vərəqlə", "Toner", "Sheets", 200, 50))).Should().Be(HttpStatusCode.BadRequest);
        (await CreateAsync(Rule("Çox faiz", "Toner", "Percent", 150, 3))).Should().Be(HttpStatusCode.BadRequest);
        (await CreateAsync(Rule("Bilinməyən", "Ink", "Percent", 15, 3))).Should().Be(HttpStatusCode.BadRequest);
        (await CreateAsync(Rule("Kanalsız", "PaperA4", "Sheets", 200, 50, channels: []))).Should().Be(HttpStatusCode.BadRequest);
        (await CreateAsync(Rule("Olmayan kiosk", "PaperA4", "Sheets", 200, 50, kioskIds: [Guid.NewGuid()]))).Should().Be(HttpStatusCode.NotFound);

        // An operator reads the rules and writes none.
        (await operatorClient.GetAsync("/api/admin/supply-rules")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(operatorClient, HttpMethod.Post, "/api/admin/supply-rules", Rule("Operatorun", "PaperA4", "Sheets", 200, 50)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var seeded = await admin.GetFromJsonAsync<JsonElement>("/api/admin/supply-rules");
        seeded.EnumerateArray().Should().Contain(rule => rule.GetProperty("resource").GetString() == "PaperA4" && rule.GetProperty("isGlobal").GetBoolean())
            .And.Contain(rule => rule.GetProperty("resource").GetString() == "Toner");
    }

    // -----------------------------------------------------------------------
    // Toner
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_toner_the_printer_reports_low_alerts_and_a_changed_cartridge_clears_it()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "supply-toner");
        var (operatorAccount, operatorClient) = await OperatorAsync(KioskScope.Only(kiosk.KioskId));
        using var _client = operatorClient;
        await using var _others = await OnlyTheseOperatorsAsync(operatorAccount.Id);
        var cartridges = await ItemAsync(admin, InventoryItemType.Toner, stock: 3, minStock: 1, models: ["Color LaserJet Pro M454"]);

        await TonerAsync(kiosk, 40);
        (await EvaluateAsync(kiosk.KioskId)).Should().Be(SupplyEvaluationResult.Nothing);

        await TonerAsync(kiosk, 12);
        (await EvaluateAsync(kiosk.KioskId)).Raised.Should().Be(1);

        var alert = (await ActiveAlertsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        alert.Resource.Should().Be(SupplyResource.Toner);
        alert.Variant.Should().Be("Black Cartridge", "a colour given as a code says nothing; the supply's name does");
        alert.Unit.Should().Be(SupplyUnit.Percent);
        alert.Level.Should().Be(SupplyAlertLevel.Warning);

        var task = await TaskAsync(alert.ServiceTaskId!.Value);
        task.Type.Should().Be(ServiceTaskType.ReplaceToner);
        task.AssignedTo.Should().Be(operatorAccount.Id);

        await TonerAsync(kiosk, 2);
        (await EvaluateAsync(kiosk.KioskId)).Changed.Should().Be(1);
        (await TaskAsync(task.Id)).Priority.Should().Be(TaskPriority.Critical);

        // "Toner dəyişdirildi": the panel offers what fits the printer.
        var fitting = await operatorClient.GetFromJsonAsync<JsonElement>(
            $"/api/admin/inventory/items?type=Toner&printerModel={Uri.EscapeDataString("Color LaserJet Pro M454dn")}");
        fitting.EnumerateArray().Should().Contain(item => item.GetProperty("id").GetGuid() == cartridges);

        (await operatorClient.GetFromJsonAsync<JsonElement>("/api/admin/inventory/items?type=Toner&printerModel=Canon"))
            .EnumerateArray().Should().NotContain(item => item.GetProperty("id").GetGuid() == cartridges);

        (await operatorClient.PostAsync($"/api/admin/service-tasks/{task.Id}/start", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CompleteAsync(operatorClient, task.Id, new { inventoryItemId = cartridges, quantity = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);

        // The change is written down next to the paper movements ...
        var supplies = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/supplies");
        supplies.GetProperty("events").EnumerateArray()
            .Should().Contain(row => row.GetProperty("type").GetString() == "Replaced" && row.GetProperty("resource").GetString() == "Toner:Black Cartridge");

        // ... and the alert stays until the printer itself reports the new cartridge.
        (await ActiveAlertsAsync(kiosk.KioskId)).Should().ContainSingle("the level is the printer's to report");

        await TonerAsync(kiosk, 100);
        (await EvaluateAsync(kiosk.KioskId)).Cleared.Should().Be(1);
        (await ActiveAlertsAsync(kiosk.KioskId)).Should().BeEmpty();

        supplies = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/supplies");
        supplies.GetProperty("levels").EnumerateArray()
            .Should().Contain(level => level.GetProperty("resource").GetString() == "Toner" && level.GetProperty("percent").GetInt32() == 100);
    }

    // -----------------------------------------------------------------------
    // Service tasks
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_task_goes_to_the_operator_with_the_least_to_do_and_waits_in_the_pool_when_there_is_none()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var first = await ReadyKioskAsync(_fixture, "supply-assign-1");
        using var second = await ReadyKioskAsync(_fixture, "supply-assign-2");
        using var nobodys = await ReadyKioskAsync(_fixture, "supply-assign-3");

        var (busy, busyClient) = await OperatorAsync(KioskScope.Only(first.KioskId, second.KioskId));
        var (free, freeClient) = await OperatorAsync(KioskScope.Only(second.KioskId));
        using var _busyClient = busyClient;
        using var _freeClient = freeClient;
        await using var _others = await OnlyTheseOperatorsAsync(busy.Id, free.Id);

        // The first kiosk has one operator: the task is theirs.
        var one = await CreateTaskAsync(admin, first.KioskId, "Cleaning");
        one.GetProperty("status").GetString().Should().Be("Assigned");
        one.GetProperty("assignedTo").GetGuid().Should().Be(busy.Id);
        one.GetProperty("source").GetString().Should().Be("Manual");

        // The second has two; the one with nothing to do gets it.
        var two = await CreateTaskAsync(admin, second.KioskId, "PrinterRepair", priority: "High");
        two.GetProperty("assignedTo").GetGuid().Should().Be(free.Id);

        // Nobody looks after the third: the pool. The SuperAdmin sees every kiosk and is not an operator.
        var three = await CreateTaskAsync(admin, nobodys.KioskId, "Other");
        three.GetProperty("status").GetString().Should().Be("Open");
        three.TryGetProperty("assignedTo", out _).Should().BeFalse("nobody's");

        // An administrator hands a task on, and can only give it to somebody who may do it there.
        var taskOne = one.GetProperty("id").GetGuid();
        (await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-tasks/{taskOne}/assign", new { assignTo = free.Id }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "the first kiosk is outside that operator's scope");

        var taskTwo = two.GetProperty("id").GetGuid();
        var handedOn = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-tasks/{taskTwo}/assign", new { assignTo = busy.Id });
        handedOn.StatusCode.Should().Be(HttpStatusCode.OK, await handedOn.Content.ReadAsStringAsync());
        (await handedOn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assignedTo").GetGuid().Should().Be(busy.Id);

        // The one it was taken from cannot start it any more; the pool's task is for whoever sees its kiosk.
        (await freeClient.PostAsync($"/api/admin/service-tasks/{taskTwo}/start", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await freeClient.PostAsync($"/api/admin/service-tasks/{three.GetProperty("id").GetGuid()}/start", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "the third kiosk is not theirs to see");

        // An operator neither opens, nor assigns, nor cancels.
        (await SendAsync(busyClient, HttpMethod.Post, "/api/admin/service-tasks", new { kioskId = first.KioskId, type = "Cleaning" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(busyClient, HttpMethod.Post, $"/api/admin/service-tasks/{taskOne}/cancel", new { reason = "istəmirəm" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // An administrator rewrites a task that is still to be done; an operator does not.
        var edited = await SendAsync(admin, HttpMethod.Put, $"/api/admin/service-tasks/{taskOne}", new { priority = "Low", notes = "Tələsik deyil", dueAt = DateTime.UtcNow.AddDays(2) });
        edited.StatusCode.Should().Be(HttpStatusCode.OK, await edited.Content.ReadAsStringAsync());
        (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("priority").GetString().Should().Be("Low");
        (await SendAsync(admin, HttpMethod.Put, $"/api/admin/service-tasks/{taskOne}", new { priority = "Urgent" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(busyClient, HttpMethod.Put, $"/api/admin/service-tasks/{taskOne}", new { priority = "High" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Cancelling needs a reason, and says who did it.
        (await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-tasks/{taskOne}/cancel", new { reason = " " }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var cancelled = await SendAsync(admin, HttpMethod.Post, $"/api/admin/service-tasks/{taskOne}/cancel", new { reason = "Kiosk köçürülür" });
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Cancelled");
        (await AuditActionsAsync(taskOne)).Should().Contain(["serviceTasks.create", "serviceTasks.update", "serviceTasks.cancel"]);
        (await SendAsync(admin, HttpMethod.Put, $"/api/admin/service-tasks/{taskOne}", new { priority = "High" })).StatusCode.Should().Be(HttpStatusCode.Conflict, "a cancelled task is over");

        // The list an operator sees is their kiosks'.
        var seen = await freeClient.GetFromJsonAsync<JsonElement>("/api/admin/service-tasks?pageSize=100");
        seen.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("kioskId").GetGuid())
            .Should().OnlyContain(id => id == second.KioskId);
    }

    [DockerFact]
    public async Task A_critical_task_nobody_starts_is_put_before_the_administrators_once()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "supply-escalate");
        var (operatorAccount, operatorClient) = await OperatorAsync(KioskScope.Only(kiosk.KioskId));
        using var _client = operatorClient;
        await using var _others = await OnlyTheseOperatorsAsync(operatorAccount.Id);

        var task = await CreateTaskAsync(admin, kiosk.KioskId, "PrinterRepair", priority: "Critical");
        var taskId = task.GetProperty("id").GetGuid();

        // Not yet: it has only just become Critical.
        await SendAsync(new EscalateOverdueServiceTasksCommand());
        (await OverdueAsync(taskId)).Should().BeEmpty();

        // Two hours later, still not started (the default wait is one hour).
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().ServiceTasks.UpdateOneAsync(
                Builders<ServiceTask>.Filter.Eq(t => t.Id, taskId),
                Builders<ServiceTask>.Update.Set(t => t.CriticalSince, DateTime.UtcNow.AddHours(-2)));
        }

        (await SendAsync(new EscalateOverdueServiceTasksCommand())).Should().BeGreaterThanOrEqualTo(1);

        var incident = (await OverdueAsync(taskId)).Should().ContainSingle().Subject;
        incident.Severity.Should().Be(IncidentSeverity.High);
        incident.KioskId.Should().Be(kiosk.KioskId);
        incident.Open.Should().BeTrue();
        (await TaskAsync(taskId)).EscalatedAt.Should().NotBeNull();

        // The next run does not say it again.
        await SendAsync(new EscalateOverdueServiceTasksCommand());
        (await OverdueAsync(taskId)).Should().ContainSingle();

        // Started: the incident is over.
        (await operatorClient.PostAsync($"/api/admin/service-tasks/{taskId}/start", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OverdueAsync(taskId)).Should().ContainSingle().Which.Open.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Forecast
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_tray_that_will_run_out_soon_gets_its_task_before_any_threshold_is_crossed()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "supply-forecast");

        // 500 sheets in the tray, 800 a day for two weeks: well above the 200
        // of the rule, and empty in fifteen hours - inside the day the forecast looks ahead.
        using (var scope = _fixture.Services.CreateScope())
        {
            var now = DateTime.UtcNow;
            await scope.ServiceProvider.GetRequiredService<MongoContext>().SupplyEvents.InsertManyAsync(
                Enumerable.Range(0, SupplyForecaster.Days).Select(day => new SupplyEvent
                {
                    KioskId = kiosk.KioskId,
                    Resource = "PaperA4",
                    Type = SupplyEventType.JobPrinted,
                    Delta = -800,
                    Before = 1300,
                    After = 500,
                    At = now.AddDays(-day).AddMinutes(-1),
                    DedupKey = $"forecast-test:{Guid.NewGuid():N}",
                }));
        }

        var supplies = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/supplies");
        var forecast = supplies.GetProperty("forecasts").EnumerateArray().Single(row => row.GetProperty("resource").GetString() == "PaperA4");

        forecast.GetProperty("perDay").GetDecimal().Should().BeApproximately(800, 60);
        forecast.GetProperty("runsOutAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddHours(15), TimeSpan.FromHours(3));
        supplies.GetProperty("alerts").GetArrayLength().Should().Be(0, "no threshold is crossed");

        (await SendAsync(new RunSupplyForecastCommand())).Should().BeGreaterThanOrEqualTo(1);

        var tasks = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/service-tasks?kioskId={kiosk.KioskId}");
        var task = tasks.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;

        task.GetProperty("type").GetString().Should().Be("RefillPaper");
        task.GetProperty("source").GetString().Should().Be("Forecast");
        task.GetProperty("priority").GetString().Should().Be("Normal");
        task.GetProperty("notes").GetString().Should().Contain("A4 kağız").And.Contain("proqnoza görə");
        task.GetProperty("dueAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        // An hour later the forecast is the same: still one task.
        await SendAsync(new RunSupplyForecastCommand());
        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/service-tasks?kioskId={kiosk.KioskId}"))
            .GetProperty("total").GetInt64().Should().Be(1);

        // And when the tray then falls below its rule, the alert takes the same task and makes it urgent.
        (await RefillAsync(admin, kiosk.KioskId, "Exact", 40)).EnsureSuccessStatusCode();
        await EvaluateAsync(kiosk.KioskId);

        var alert = (await ActiveAlertsAsync(kiosk.KioskId)).Should().ContainSingle().Subject;
        alert.ServiceTaskId.Should().Be(task.GetProperty("id").GetGuid());
        (await TaskAsync(alert.ServiceTaskId!.Value)).Priority.Should().Be(TaskPriority.Critical);
    }

    // -----------------------------------------------------------------------
    // The warehouse
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task The_warehouse_counts_by_movements_refuses_what_it_does_not_have_and_says_when_it_runs_low()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var (_, operatorClient) = await OperatorAsync(KioskScope.Everything());
        using var _client = operatorClient;

        var sku = $"TEST-{Guid.NewGuid():N}"[..20];
        var created = await SendAsync(admin, HttpMethod.Post, "/api/admin/inventory/items", Item(sku, "Paper A3, 500", "PaperA3Ream", minStock: 4));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The same code twice is one item too many.
        (await SendAsync(admin, HttpMethod.Post, "/api/admin/inventory/items", Item(sku, "Another", "PaperA3Ream")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await SendAsync(admin, HttpMethod.Post, "/api/admin/inventory/items", Item("", "No code", "PaperA3Ream")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(admin, HttpMethod.Post, "/api/admin/inventory/items", Item($"X-{Guid.NewGuid():N}"[..12], "Bad type", "Staples")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        async Task<HttpResponseMessage> MoveAsync(HttpClient client, string type, decimal quantity)
            => await SendAsync(client, HttpMethod.Post, "/api/admin/inventory/movements", new { itemId, type, quantity, note = "test" });

        // New, with nothing on the shelf: below its minimum from the start.
        await SendAsync(new CheckInventoryLowStockCommand());
        (await LowStockAsync(itemId)).Should().ContainSingle().Which.Open.Should().BeTrue();

        (await MoveAsync(admin, "Purchase", 10)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LowStockAsync(itemId)).Should().ContainSingle().Which.Open.Should().BeFalse("ten is above the minimum of four");

        (await MoveAsync(admin, "UsedAtKiosk", 11)).StatusCode.Should().Be(HttpStatusCode.Conflict, "there are ten");
        (await MoveAsync(admin, "Purchase", 0)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MoveAsync(admin, "Purchase", -2)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MoveAsync(admin, "Stolen", 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await MoveAsync(admin, "IssueToOperator", 5)).StatusCode.Should().Be(HttpStatusCode.OK);
        var low = await MoveAsync(admin, "WriteOff", 2);
        var item = await low.Content.ReadFromJsonAsync<JsonElement>();

        item.GetProperty("stock").GetDecimal().Should().Be(3);
        item.GetProperty("isLow").GetBoolean().Should().BeTrue();

        // Said at once, not an hour later - and once, as a High incident about no kiosk.
        var incidents = await LowStockAsync(itemId);
        incidents.Count(incident => incident.Open).Should().Be(1);
        incidents.Single(incident => incident.Open).Severity.Should().Be(IncidentSeverity.High);
        incidents.Single(incident => incident.Open).KioskId.Should().BeNull();

        await SendAsync(new CheckInventoryLowStockCommand());
        (await LowStockAsync(itemId)).Count(incident => incident.Open).Should().Be(1);

        // A recount finds two more than the book says.
        (await MoveAsync(admin, "Adjustment", 2)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LowStockAsync(itemId)).Should().NotContain(incident => incident.Open, "five is not below four");

        var movements = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/inventory/movements?itemId={itemId}");
        movements.GetProperty("total").GetInt64().Should().Be(4);
        movements.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("quantity").GetDecimal())
            .Should().Equal(2, -2, -5, 10);

        // An operator reads the warehouse and moves nothing by hand.
        (await operatorClient.GetAsync("/api/admin/inventory/items")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await MoveAsync(operatorClient, "Purchase", 1)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static object Rule(
        string name, string resource, string unit, decimal warning, decimal critical, Guid[]? kioskIds = null, string[]? channels = null)
        => new
        {
            name,
            kioskIds = kioskIds ?? [],
            resource,
            resourceVariant = (string?)null,
            unit,
            warningThreshold = warning,
            criticalThreshold = critical,
            channels = channels ?? ["AdminPanel", "OperatorPanel"],
            createServiceTask = true,
            taskPriority = "High",
            isActive = true,
        };

    private static object Item(string sku, string name, string type, decimal minStock = 0, int? sheetsPerUnit = 500, string[]? models = null)
        => new { sku, name, type, compatiblePrinterModels = models ?? [], unit = "bağlama", sheetsPerUnit, minStock, location = "Anbar", isActive = true };

    private async Task<Guid> ItemAsync(
        HttpClient admin, InventoryItemType type, decimal stock, decimal minStock, int? sheetsPerUnit = null, string[]? models = null)
    {
        var created = await SendAsync(
            admin, HttpMethod.Post, "/api/admin/inventory/items", Item($"T-{Guid.NewGuid():N}"[..18], $"Test {type}", type.ToString(), minStock, sheetsPerUnit, models));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await SendAsync(admin, HttpMethod.Post, "/api/admin/inventory/movements", new { itemId = id, type = "Purchase", quantity = stock }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        return id;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RefillAsync(HttpClient admin, Guid kioskId, string mode, int sheets)
        => SendAsync(admin, HttpMethod.Post, $"/api/admin/kiosks/{kioskId}/paper/refill", new { paper = "A4", mode, sheets });

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid taskId, object body)
        => SendAsync(client, HttpMethod.Post, $"/api/admin/service-tasks/{taskId}/complete", body);

    private static async Task<JsonElement> CreateTaskAsync(HttpClient admin, Guid kioskId, string type, string? priority = null)
    {
        var created = await SendAsync(admin, HttpMethod.Post, "/api/admin/service-tasks", new { kioskId, type, priority, notes = "Test tapşırığı" });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        return await created.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A paid order that prints this many sheets: the tray's counter falls by them.</summary>
    private async Task PrintAsync(TestKiosk kiosk, int sheets)
    {
        var (jobId, _, _) = await PaidBankJobAsync(_fixture, kiosk, pages: sheets);

        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Completed", sides: sheets, sheets: sheets);
    }

    /// <summary>The kiosk's selected printer says how much black toner it has.</summary>
    private static async Task TonerAsync(TestKiosk kiosk, int percent)
    {
        var response = await kiosk.Client.PostAsJsonAsync("/api/kiosks/me/printers/telemetry", new
        {
            printers = new[]
            {
                new
                {
                    name = "Test printer",
                    manufacturer = "HP",
                    model = "Color LaserJet Pro M454dn",
                    state = "idle",
                    stateReasons = Array.Empty<string>(),
                    online = true,
                    supplies = new object[]
                    {
                        new { name = "Black Cartridge", type = "toner", color = "#000000", levelPercent = percent },
                        new { name = "Imaging Drum", type = "drum" },
                    },
                    trays = new[] { new { name = "Tray 1", media = "A4", levelPercent = 60 } },
                    lifetimePages = 48_213L,
                    spoolerJobs = Array.Empty<object>(),
                },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private async Task<TResult> SendAsync<TResult>(IRequest<TResult> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private Task<SupplyEvaluationResult> EvaluateAsync(Guid kioskId) => SendAsync(new EvaluateKioskSuppliesCommand(kioskId));

    private async Task<List<SupplyAlert>> ActiveAlertsAsync(Guid kioskId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().SupplyAlerts
            .Find(alert => alert.KioskId == kioskId && alert.Active).ToListAsync();
    }

    private async Task<ServiceTask> TaskAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().ServiceTasks.Find(task => task.Id == id).SingleAsync();
    }

    private async Task<Kiosk> KioskAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks.Find(kiosk => kiosk.Id == id).SingleAsync();
    }

    private async Task<List<Incident>> OverdueAsync(Guid taskId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
            .Find(incident => incident.DedupKey == ServiceTaskHandlers.OverdueKey(taskId)).ToListAsync();
    }

    private async Task<List<Incident>> LowStockAsync(Guid itemId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents
            .Find(incident => incident.DedupKey == InventoryHandlers.LowStockKey(itemId)).ToListAsync();
    }

    private async Task<List<string>> AuditActionsAsync(Guid entityId)
    {
        using var scope = _fixture.Services.CreateScope();
        var id = entityId.ToString();

        return await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(entry => entry.EntityId == id).Project(entry => entry.Action).ToListAsync();
    }

    private static string Token(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    /// <summary>An operator: an account that sees these kiosks, finishes tasks and does not hand them out - signed in.</summary>
    private async Task<(AdminUser Account, HttpClient Client)> OperatorAsync(KioskScope kioskScope)
    {
        using var superAdmin = await TestKiosks.SuperAdminAsync(_fixture);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Supply operator {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions = OperatorPermissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var scope = _fixture.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();

        var account = new AdminUser
        {
            Email = $"supply.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Supply operator",
            RoleIds = [(await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()],
            KioskScope = kioskScope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, OperatorPassword);

        await scope.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password = OperatorPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return (account, client);
    }

    /// <summary>
    /// For the length of a test, these are the only operators there are: every
    /// other account that could be given a task - the development operator,
    /// the ones other tests made - is switched off, and switched on again at
    /// the end. Who gets a task is then the test's to say.
    /// </summary>
    private async Task<IAsyncDisposable> OnlyTheseOperatorsAsync(params Guid[] keep)
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var desk = scope.ServiceProvider.GetRequiredService<ServiceTaskDesk>();
        var others = new List<Guid>();

        foreach (var admin in await context.AdminUsers.Find(admin => admin.IsActive).ToListAsync())
        {
            // Asked about a kiosk that does not exist: only an account that sees every kiosk can do it there,
            // and for the scoped ones of other tests their own kiosks are not this test's.
            if (!keep.Contains(admin.Id) && await desk.CanDoAsync(admin.Id, Guid.Empty, CancellationToken.None))
            {
                others.Add(admin.Id);
            }
        }

        await SetActiveAsync(context, others, active: false);

        return new Restore(async () =>
        {
            using var again = _fixture.Services.CreateScope();
            await SetActiveAsync(again.ServiceProvider.GetRequiredService<MongoContext>(), others, active: true);
        });
    }

    private static Task SetActiveAsync(MongoContext context, List<Guid> admins, bool active)
        => admins.Count == 0
            ? Task.CompletedTask
            : context.AdminUsers.UpdateManyAsync(
                Builders<AdminUser>.Filter.In(admin => admin.Id, admins), Builders<AdminUser>.Update.Set(admin => admin.IsActive, active));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
