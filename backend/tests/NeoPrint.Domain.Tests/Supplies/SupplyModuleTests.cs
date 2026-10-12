using NeoPrint.Domain.Common;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Domain.Tests.Supplies;

/// <summary>
/// The supply module's rules (plan section 2.15): a threshold rule, the alert
/// it raises, the service task and the warehouse's count.
/// </summary>
public sealed class SupplyModuleTests
{
    // Every time in these tests is this one or counted from it: nothing here reads the real clock.
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Kiosk = Guid.NewGuid();

    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-supply");

    private static readonly ActorContext System = ActorContext.System("serviceTasks.reason.autoAssigned");

    private static SupplyAlertRule PaperRule() => new()
    {
        Name = "Kağız A4",
        Resource = SupplyResource.PaperA4,
        Unit = SupplyUnit.Sheets,
        WarningThreshold = 200,
        CriticalThreshold = 50,
        CreateServiceTask = true,
        CreatedAt = Now,
    };

    // -----------------------------------------------------------------------
    // Rules
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(500, null)]
    [InlineData(200, null)]
    [InlineData(199, SupplyAlertLevel.Warning)]
    [InlineData(50, SupplyAlertLevel.Warning)]
    [InlineData(49, SupplyAlertLevel.Critical)]
    [InlineData(0, SupplyAlertLevel.Critical)]
    public void A_level_is_below_a_threshold_only_when_it_is_less_than_it(int sheets, SupplyAlertLevel? expected)
        => PaperRule().LevelOf(sheets).Should().Be(expected, "\"200-dən aşağı\": a tray with exactly 200 sheets is not below 200");

    [Fact]
    public void A_rule_watches_its_resource_and_its_colour_when_it_names_one()
    {
        var anyToner = new SupplyAlertRule { Resource = SupplyResource.Toner };
        var black = new SupplyAlertRule { Resource = SupplyResource.Toner, ResourceVariant = "Black" };

        anyToner.Watches(SupplyResource.Toner, "cyan").Should().BeTrue();
        anyToner.Watches(SupplyResource.Drum, "black").Should().BeFalse();
        black.Watches(SupplyResource.Toner, "black").Should().BeTrue("a colour is the same in any case");
        black.Watches(SupplyResource.Toner, "cyan").Should().BeFalse();
        black.Watches(SupplyResource.Toner, null).Should().BeFalse();
    }

    [Fact]
    public void A_rule_that_could_never_fire_is_refused()
    {
        var problems = new Dictionary<string, Action<SupplyAlertRule>>
        {
            ["supplyRules.nameInvalid"] = rule => rule.Name = " ",
            ["supplyRules.thresholdsOrder"] = rule => rule.WarningThreshold = 50,
            ["supplyRules.percentTooHigh"] = rule => { rule.Unit = SupplyUnit.Percent; rule.WarningThreshold = 120; },
            ["supplyRules.sheetsWhole"] = rule => rule.CriticalThreshold = 49.5m,
            ["supplyRules.paperHasNoVariant"] = rule => rule.ResourceVariant = "black",
            ["supplyRules.channelRequired"] = rule => rule.Channels = AlertChannels.None,
            ["supplyRules.unitPercentOnly"] = rule => rule.Resource = SupplyResource.Toner,
        };

        foreach (var (key, breakIt) in problems)
        {
            var rule = PaperRule();
            breakIt(rule);

            rule.Invoking(r => r.EnsureValid()).Should().Throw<DomainException>().Which.Text.Key.Should().Be(key);
        }

        var zero = PaperRule();
        zero.CriticalThreshold = 0;
        zero.Invoking(r => r.EnsureValid()).Should().Throw<DomainException>("nothing is below zero: the rule would never be critical");

        PaperRule().Invoking(r => r.EnsureValid()).Should().NotThrow();
    }

    // -----------------------------------------------------------------------
    // Alerts
    // -----------------------------------------------------------------------

    [Fact]
    public void An_alert_is_told_about_once_per_level_and_clears_when_the_level_recovers()
    {
        var alert = SupplyAlert.Raise(Kiosk, PaperRule(), null, SupplyAlertLevel.Warning, 180, Now);

        alert.Active.Should().BeTrue();
        alert.Key.Should().Be(SupplyAlert.KeyOf(Kiosk, SupplyResource.PaperA4, null));

        // Still low, a little lower: the number follows, nobody is told again.
        alert.Observe(SupplyAlertLevel.Warning, 150, Now.AddMinutes(5)).Should().BeFalse();
        alert.Value.Should().Be(150);
        alert.LevelSince.Should().Be(Now);

        alert.Observe(SupplyAlertLevel.Critical, 40, Now.AddMinutes(10)).Should().BeTrue("the level changed");
        alert.LevelSince.Should().Be(Now.AddMinutes(10));

        alert.Clear(500, Now.AddHours(1));

        alert.Active.Should().BeFalse();
        alert.ClearedAt.Should().Be(Now.AddHours(1));
        alert.Observe(SupplyAlertLevel.Warning, 100, Now.AddHours(2)).Should().BeFalse("a cleared alert is over; a new fall is a new alert");
    }

    [Fact]
    public void Two_toners_of_one_kiosk_are_two_alerts_and_a_colour_is_one_whatever_its_case()
    {
        SupplyAlert.KeyOf(Kiosk, SupplyResource.Toner, "Black").Should().Be(SupplyAlert.KeyOf(Kiosk, SupplyResource.Toner, " black "));
        SupplyAlert.KeyOf(Kiosk, SupplyResource.Toner, "black").Should().NotBe(SupplyAlert.KeyOf(Kiosk, SupplyResource.Toner, "cyan"));
        SupplyAlert.KeyOf(Kiosk, SupplyResource.PaperA4, null).Should().NotBe(SupplyAlert.KeyOf(Kiosk, SupplyResource.PaperA3, null));
    }

    // -----------------------------------------------------------------------
    // Service tasks
    // -----------------------------------------------------------------------

    private static ServiceTask Refill(TaskPriority priority = TaskPriority.High)
        => ServiceTask.Open(Kiosk, ServiceTaskType.RefillPaper, priority, ServiceTaskSource.AlertRule, Now, SupplyResource.PaperA4);

    [Fact]
    public void A_task_goes_from_the_pool_to_an_operator_who_starts_and_finishes_it()
    {
        var operatorId = Guid.NewGuid();
        var task = Refill();

        task.Status.Should().Be(ServiceTaskStatus.Open);
        task.Active.Should().BeTrue();

        task.Assign(operatorId, System, Now.AddMinutes(1));
        task.Status.Should().Be(ServiceTaskStatus.Assigned);
        task.AssignedAt.Should().Be(Now.AddMinutes(1));

        task.Start(operatorId, Admin, Now.AddMinutes(30));
        task.Status.Should().Be(ServiceTaskStatus.InProgress);
        task.StartedAt.Should().Be(Now.AddMinutes(30));

        task.Complete(new ServiceTaskCompletion { Paper = "A4", SheetsAdded = 450 }, Admin, Now.AddMinutes(40));

        task.Status.Should().Be(ServiceTaskStatus.Done);
        task.Active.Should().BeFalse();
        task.CompletedAt.Should().Be(Now.AddMinutes(40));
        task.Completion!.SheetsAdded.Should().Be(450);

        task.PendingTransitions.Select(transition => transition.ToStatus)
            .Should().Equal(nameof(ServiceTaskStatus.Assigned), nameof(ServiceTaskStatus.InProgress), nameof(ServiceTaskStatus.Done));
    }

    [Fact]
    public void A_task_in_the_pool_becomes_whoever_starts_it_and_another_s_task_cannot_be_started()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var pooled = Refill();
        pooled.Start(first, Admin, Now.AddMinutes(1));
        pooled.AssignedTo.Should().Be(first);

        var theirs = Refill();
        theirs.Assign(first, System, Now);

        theirs.Invoking(t => t.Start(second, Admin, Now.AddMinutes(1)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("serviceTasks.notYours");
    }

    [Fact]
    public void An_assigned_task_can_be_handed_on_or_put_back_but_not_once_it_is_started()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var task = Refill();

        task.Assign(first, System, Now);
        task.Assign(second, Admin, Now.AddMinutes(1));
        task.AssignedTo.Should().Be(second);
        task.Status.Should().Be(ServiceTaskStatus.Assigned);

        task.Assign(null, Admin, Now.AddMinutes(2));
        task.Status.Should().Be(ServiceTaskStatus.Open, "back in the pool");
        task.AssignedTo.Should().BeNull();

        task.Start(first, Admin, Now.AddMinutes(3));

        task.Invoking(t => t.Assign(second, Admin, Now.AddMinutes(4)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("serviceTasks.notAssignable");
    }

    [Fact]
    public void A_task_cannot_be_finished_before_it_is_started_or_cancelled_after_it_is_done()
    {
        var task = Refill();

        task.Invoking(t => t.Complete(new ServiceTaskCompletion(), Admin, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("serviceTasks.notInProgress");

        task.Start(Guid.NewGuid(), Admin, Now);
        task.Complete(new ServiceTaskCompletion(), Admin, Now.AddMinutes(5));

        task.Invoking(t => t.Cancel("no longer needed", Admin, Now.AddMinutes(6)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("serviceTasks.alreadyDone");
    }

    [Fact]
    public void Cancelling_ends_a_task_and_saying_it_twice_is_once()
    {
        var task = Refill();

        task.Cancel("the kiosk was taken away", Admin, Now.AddMinutes(1));
        var transitions = task.PendingTransitions.Count;

        task.Cancel("again", Admin, Now.AddMinutes(2));

        task.Status.Should().Be(ServiceTaskStatus.Cancelled);
        task.Active.Should().BeFalse();
        task.CancelReason.Should().Be("the kiosk was taken away");
        task.PendingTransitions.Should().HaveCount(transitions);
    }

    [Fact]
    public void A_task_only_becomes_more_urgent_and_is_escalated_once_after_waiting_as_critical()
    {
        var wait = TimeSpan.FromMinutes(60);
        var task = Refill(TaskPriority.High);

        task.RaisePriority(TaskPriority.Normal, Now).Should().BeFalse("an alert that eases does not lower a task");
        task.Priority.Should().Be(TaskPriority.High);

        // A day as High changes nothing: only a Critical task is escalated.
        task.NeedsEscalation(Now.AddDays(1), wait).Should().BeFalse();

        task.RaisePriority(TaskPriority.Critical, Now.AddDays(1)).Should().BeTrue();

        task.NeedsEscalation(Now.AddDays(1).AddMinutes(59), wait).Should().BeFalse("the wait is counted from when it became Critical");
        task.NeedsEscalation(Now.AddDays(1).AddMinutes(61), wait).Should().BeTrue();

        task.MarkEscalated(Now.AddDays(1).AddMinutes(61));
        task.NeedsEscalation(Now.AddDays(2), wait).Should().BeFalse("the administrators are told once");

        var started = Refill(TaskPriority.Critical);
        started.Start(Guid.NewGuid(), Admin, Now.AddMinutes(5));
        started.NeedsEscalation(Now.AddHours(5), wait).Should().BeFalse("somebody is at it");
    }

    [Fact]
    public void An_administrator_can_rewrite_a_task_that_is_still_to_be_done()
    {
        var wait = TimeSpan.FromMinutes(60);
        var task = Refill(TaskPriority.Critical);
        task.MarkEscalated(Now.AddHours(2));

        // No longer urgent: the escalation has nothing more to say.
        task.Edit(TaskPriority.Normal, "Sabah da olar", Now.AddDays(1), Now.AddHours(3));

        task.Priority.Should().Be(TaskPriority.Normal);
        task.CriticalSince.Should().BeNull();
        task.Notes.Should().Be("Sabah da olar");
        task.DueAt.Should().Be(Now.AddDays(1));

        // Urgent again: the wait is counted afresh, and it may be escalated again.
        task.Edit(TaskPriority.Critical, null, null, Now.AddHours(4));

        task.NeedsEscalation(Now.AddHours(4).AddMinutes(30), wait).Should().BeFalse();
        task.NeedsEscalation(Now.AddHours(5).AddMinutes(1), wait).Should().BeTrue();

        task.Cancel("not needed", Admin, Now.AddHours(6));
        task.Invoking(t => t.Edit(TaskPriority.Low, null, null, Now.AddHours(7)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("serviceTasks.notEditable");
    }

    [Fact]
    public void A_task_the_system_opens_has_a_key_and_one_opened_by_hand_has_none()
    {
        Refill().Key.Should().Be(ServiceTask.KeyOf(Kiosk, ServiceTaskType.RefillPaper, "PaperA4"));

        ServiceTask.Open(Kiosk, ServiceTaskType.RefillPaper, TaskPriority.High, ServiceTaskSource.AlertRule, Now, SupplyResource.PaperA3)
            .Key.Should().NotBe(Refill().Key, "A4 and A3 are two trays");

        ServiceTask.Open(Kiosk, ServiceTaskType.ReplaceToner, TaskPriority.High, ServiceTaskSource.Forecast, Now, SupplyResource.Toner, "Black")
            .Key.Should().Be(ServiceTask.KeyOf(Kiosk, ServiceTaskType.ReplaceToner, "black"));

        ServiceTask.Open(Kiosk, ServiceTaskType.Cleaning, TaskPriority.Low, ServiceTaskSource.Manual, Now)
            .Key.Should().BeNull("an administrator may open as many as they like");
    }

    // -----------------------------------------------------------------------
    // The warehouse
    // -----------------------------------------------------------------------

    private static InventoryItem Reams(decimal minStock = 5) => new()
    {
        Sku = "PAPER-A4-500",
        Name = "A4 kağız, 500 vərəq",
        Type = InventoryItemType.PaperA4Ream,
        Unit = "bağlama",
        SheetsPerUnit = 500,
        MinStock = minStock,
        CreatedAt = Now,
    };

    [Fact]
    public void The_count_moves_only_through_a_movement_and_each_says_what_was_left()
    {
        var item = Reams();
        var by = Guid.NewGuid();

        var bought = item.Move(InventoryMovementType.Purchase, 20, by, Now);
        bought.Quantity.Should().Be(20);
        bought.StockAfter.Should().Be(20);

        var used = item.Move(InventoryMovementType.UsedAtKiosk, 1, by, Now.AddHours(1), Kiosk, Guid.NewGuid());
        used.Quantity.Should().Be(-1, "what is taken is written as taken");
        used.KioskId.Should().Be(Kiosk);

        item.Move(InventoryMovementType.IssueToOperator, 3, by, Now.AddHours(2));
        item.Move(InventoryMovementType.WriteOff, 1, by, Now.AddHours(3));
        item.Move(InventoryMovementType.Adjustment, -2, by, Now.AddHours(4)).Quantity.Should().Be(-2);
        item.Move(InventoryMovementType.Adjustment, 1, by, Now.AddHours(5));

        item.Stock.Should().Be(14);
    }

    [Fact]
    public void More_than_the_shelf_holds_cannot_be_taken_and_nothing_cannot_be_moved()
    {
        var item = Reams();
        item.Move(InventoryMovementType.Purchase, 2, null, Now);

        item.Invoking(i => i.Move(InventoryMovementType.UsedAtKiosk, 3, null, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("inventory.notEnough");
        item.Stock.Should().Be(2, "a refused movement moves nothing");

        item.Invoking(i => i.Move(InventoryMovementType.Purchase, 0, null, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("inventory.quantityInvalid");
        item.Invoking(i => i.Move(InventoryMovementType.WriteOff, -1, null, Now))
            .Should().Throw<DomainException>("only an adjustment has a direction");
    }

    [Fact]
    public void An_item_is_low_from_the_movement_that_took_it_below_its_minimum_until_it_is_back()
    {
        var item = Reams(minStock: 5);

        item.Move(InventoryMovementType.Purchase, 6, null, Now);
        item.IsLow.Should().BeFalse();
        item.LowSince.Should().BeNull();

        item.Move(InventoryMovementType.UsedAtKiosk, 2, null, Now.AddHours(1));
        item.IsLow.Should().BeTrue();
        item.LowSince.Should().Be(Now.AddHours(1));

        item.Move(InventoryMovementType.UsedAtKiosk, 1, null, Now.AddHours(2));
        item.LowSince.Should().Be(Now.AddHours(1), "since it first went below");

        item.Move(InventoryMovementType.Purchase, 10, null, Now.AddHours(3));
        item.IsLow.Should().BeFalse();
        item.LowSince.Should().BeNull();
    }

    [Fact]
    public void A_cartridge_fits_the_printers_it_names_and_paper_fits_any()
    {
        var toner = new InventoryItem { CompatiblePrinterModels = ["LaserJet Pro M404", "M428"] };

        toner.Fits("HP LaserJet Pro M404dn").Should().BeTrue();
        toner.Fits("hp laserjet pro mfp m428fdw").Should().BeTrue();
        toner.Fits("Canon LBP226").Should().BeFalse();
        toner.Fits(null).Should().BeFalse("a cartridge is not offered for a printer nobody knows");

        Reams().Fits("Canon LBP226").Should().BeTrue();
        Reams().Fits(null).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // The forecast
    // -----------------------------------------------------------------------

    [Fact]
    public void The_daily_average_weighs_recent_days_more()
    {
        SupplyForecaster.WeightedDailyAverage([]).Should().Be(0);
        SupplyForecaster.WeightedDailyAverage([10, 10, 10]).Should().Be(10);

        // 0 two days ago, 0 yesterday, 60 today: (0·1 + 0·2 + 60·3) / 6.
        SupplyForecaster.WeightedDailyAverage([0, 0, 60]).Should().Be(30);

        // The same sheets, printed long ago, count for less.
        SupplyForecaster.WeightedDailyAverage([60, 0, 0]).Should().Be(10);
    }

    [Fact]
    public void Paper_runs_out_when_what_is_left_is_divided_by_what_goes_in_a_day()
    {
        var daily = Enumerable.Repeat(100m, SupplyForecaster.Days).ToList();

        var forecast = SupplyForecaster.ForPaper(250, daily, Now);

        forecast.PerDay.Should().Be(100);
        forecast.RunsOutAt.Should().Be(Now.AddDays(2.5));

        SupplyForecaster.ForPaper(250, Enumerable.Repeat(0m, SupplyForecaster.Days).ToList(), Now)
            .RunsOutAt.Should().BeNull("a kiosk nobody prints at does not run out");

        SupplyForecaster.ForPaper(0, daily, Now).RunsOutAt.Should().Be(Now, "an empty tray has run out");

        SupplyForecaster.ForPaper(500, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1], Now)
            .RunsOutAt.Should().BeNull("more than a year away has no date");
    }

    [Fact]
    public void A_cartridge_runs_out_by_what_a_sheet_costs_it_and_a_new_cartridge_is_not_negative_use()
    {
        var daily = Enumerable.Repeat(100m, SupplyForecaster.Days).ToList();

        // 40 → 30 over 1 400 sheets: 10 % per 1 400 sheets, 100 sheets a day → 0.714 % a day; 30 % lasts 42 days.
        var forecast = SupplyForecaster.ForPercent(30, [40, 38, 35, 32, 30], 1400, daily, Now);

        forecast.RunsOutAt.Should().BeCloseTo(Now.AddDays(42), TimeSpan.FromMinutes(1));

        // Replaced in the middle: 12 → 8, then 100 → 96. The rise is not counted; the falls are 8 %.
        var replaced = SupplyForecaster.ForPercent(96, [12, 8, 100, 96], 1400, daily, Now);

        replaced.PerDay.Should().BeApproximately(8m / 1400 * 100, 0.0001m);

        SupplyForecaster.ForPercent(50, [50, 50, 50], 1400, daily, Now).RunsOutAt.Should().BeNull("a level that did not move says nothing");
        SupplyForecaster.ForPercent(50, [60, 50], 0, daily, Now).RunsOutAt.Should().BeNull("nothing was printed while it fell");
    }
}
