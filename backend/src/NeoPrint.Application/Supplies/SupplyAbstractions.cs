using NeoPrint.Application.Localization;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

// ---------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------

/// <summary>The <c>supply_alert_rules</c> collection (plan section 2.15).</summary>
public interface ISupplyAlertRuleRepository
{
    Task<IReadOnlyList<SupplyAlertRule>> ListAsync(CancellationToken cancellationToken = default);

    Task<SupplyAlertRule?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(SupplyAlertRule rule, CancellationToken cancellationToken = default);

    Task UpdateAsync(SupplyAlertRule rule, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>The <c>supply_alerts</c> collection: what is below its rule now, and what was.</summary>
public interface ISupplyAlertRepository
{
    Task<IReadOnlyList<SupplyAlert>> ListActiveForKioskAsync(Guid kioskId, CancellationToken cancellationToken = default);

    /// <returns>False when an active alert with this key is already there.</returns>
    Task<bool> TryAddAsync(SupplyAlert alert, CancellationToken cancellationToken = default);

    Task UpdateAsync(SupplyAlert alert, CancellationToken cancellationToken = default);

    /// <param name="scope">Null for every kiosk; otherwise only these (an account's kiosk scope).</param>
    Task<(IReadOnlyList<SupplyAlert> Items, long Total)> ListAsync(
        bool? active,
        Guid? kioskId,
        IReadOnlyCollection<Guid>? scope,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}

/// <summary>The <c>service_tasks</c> collection.</summary>
public interface IServiceTaskRepository
{
    Task<ServiceTask?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The task the system opened for this and nobody has finished yet.</summary>
    Task<ServiceTask?> FindActiveByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <returns>False when a task with this key is still to be done.</returns>
    Task<bool> TryAddAsync(ServiceTask task, CancellationToken cancellationToken = default);

    Task UpdateAsync(ServiceTask task, CancellationToken cancellationToken = default);

    /// <summary>Every task still to be done: for the escalation, and to see who has the fewest.</summary>
    Task<IReadOnlyList<ServiceTask>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>The most urgent first, then the oldest.</summary>
    /// <param name="scope">Null for every kiosk; otherwise only these.</param>
    /// <param name="unassignedOnly">Only the tasks nobody has been given (the pool).</param>
    Task<(IReadOnlyList<ServiceTask> Items, long Total)> ListAsync(
        IReadOnlyCollection<ServiceTaskStatus>? statuses,
        Guid? kioskId,
        Guid? assignedTo,
        IReadOnlyCollection<Guid>? scope,
        int skip,
        int take,
        CancellationToken cancellationToken = default,
        bool unassignedOnly = false);

    /// <summary>The tasks finished since then: for how long a task takes (the SLA figures).</summary>
    Task<IReadOnlyList<ServiceTask>> ListCompletedSinceAsync(DateTime since, CancellationToken cancellationToken = default);
}

/// <summary>The <c>inventory_items</c> and <c>inventory_movements</c> collections.</summary>
public interface IInventoryRepository
{
    Task<IReadOnlyList<InventoryItem>> ListAsync(CancellationToken cancellationToken = default);

    Task<InventoryItem?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<InventoryItem?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default);

    Task AddAsync(InventoryItem item, CancellationToken cancellationToken = default);

    Task UpdateAsync(InventoryItem item, CancellationToken cancellationToken = default);

    Task AddMovementAsync(InventoryMovement movement, CancellationToken cancellationToken = default);

    /// <summary>The newest first.</summary>
    /// <param name="kioskScope">Null: every movement. Otherwise those of no kiosk and of these kiosks.</param>
    Task<(IReadOnlyList<InventoryMovement> Items, long Total)> ListMovementsAsync(
        Guid? itemId, Guid? kioskId, IReadOnlyCollection<Guid>? kioskScope, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>How much paper a kiosk used, read from <c>supply_events</c>.</summary>
public interface ISupplyUsageReader
{
    /// <summary>
    /// The sheets that left the trays on each of the last <paramref name="days"/>
    /// days (UTC), oldest first; a day nothing was printed is a zero.
    /// </summary>
    /// <param name="resource"><c>PaperA4</c>, <c>PaperA3</c>, or null for every tray.</param>
    Task<IReadOnlyList<decimal>> DailySheetsAsync(
        Guid kioskId, string? resource, DateTime now, int days, CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// What the panels are shown
// ---------------------------------------------------------------------------

public sealed record SupplyRuleView(
    Guid Id,
    string Name,
    bool IsGlobal,
    IReadOnlyList<Guid> KioskIds,
    string Resource,
    string? ResourceVariant,
    string Unit,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    IReadOnlyList<string> Channels,
    bool CreateServiceTask,
    string TaskPriority,
    bool IsActive)
{
    public static SupplyRuleView Of(SupplyAlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new SupplyRuleView(
            rule.Id,
            rule.Name,
            rule.IsGlobal,
            rule.KioskIds,
            rule.Resource.ToString(),
            rule.ResourceVariant,
            rule.Unit.ToString(),
            rule.WarningThreshold,
            rule.CriticalThreshold,
            [.. Enum.GetValues<AlertChannels>().Where(channel => channel != AlertChannels.None && rule.Channels.HasFlag(channel)).Select(channel => channel.ToString())],
            rule.CreateServiceTask,
            rule.TaskPriority.ToString(),
            rule.IsActive);
    }
}

public sealed record SupplyAlertView(
    Guid Id,
    Guid KioskId,
    string? KioskCode,
    string Resource,
    string? Variant,
    string Unit,
    string Level,
    decimal Value,
    bool Active,
    DateTime RaisedAt,
    DateTime LevelSince,
    DateTime? ClearedAt,
    Guid? ServiceTaskId,
    Guid RuleId)
{
    public static SupplyAlertView Of(SupplyAlert alert, string? kioskCode)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new SupplyAlertView(
            alert.Id,
            alert.KioskId,
            kioskCode,
            alert.Resource.ToString(),
            alert.Variant,
            alert.Unit.ToString(),
            alert.Level.ToString(),
            alert.Value,
            alert.Active,
            alert.CreatedAt,
            alert.LevelSince,
            alert.ClearedAt,
            alert.ServiceTaskId,
            alert.RuleId);
    }
}

public sealed record ServiceTaskCompletionView(string? Paper, int? SheetsAdded, Guid? InventoryItemId, decimal? Quantity, string? Note);

public sealed record ServiceTaskView(
    Guid Id,
    Guid KioskId,
    string? KioskCode,
    string? KioskName,
    string Type,
    string Priority,
    string Status,
    string Source,
    string? Resource,
    string? Variant,
    Guid? AssignedTo,
    string? AssignedToName,
    DateTime? DueAt,
    string? Notes,
    DateTime CreatedAt,
    DateTime? AssignedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? EscalatedAt,
    string? CancelReason,
    ServiceTaskCompletionView? Completion)
{
    public static ServiceTaskView Of(ServiceTask task, Kiosk? kiosk, string? assignedToName)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new ServiceTaskView(
            task.Id,
            task.KioskId,
            kiosk?.Code,
            kiosk?.Name,
            task.Type.ToString(),
            task.Priority.ToString(),
            task.Status.ToString(),
            task.Source.ToString(),
            task.Resource?.ToString(),
            task.Variant,
            task.AssignedTo,
            assignedToName,
            task.DueAt,

            // A note the system wrote is a stored message; one a person wrote is their words.
            Messages.WriteStored(task.Notes),
            task.CreatedAt,
            task.AssignedAt,
            task.StartedAt,
            task.CompletedAt,
            task.EscalatedAt,
            Messages.WriteStored(task.CancelReason),
            task.Completion is { } done
                ? new ServiceTaskCompletionView(done.Paper, done.SheetsAdded, done.InventoryItemId, done.Quantity, done.Note)
                : null);
    }
}

public sealed record InventoryItemView(
    Guid Id,
    string Sku,
    string Name,
    string Type,
    IReadOnlyList<string> CompatiblePrinterModels,
    string Unit,
    int? SheetsPerUnit,
    decimal MinStock,
    decimal Stock,
    string Location,
    bool IsActive,
    bool IsLow,
    DateTime? LowSince)
{
    public static InventoryItemView Of(InventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new InventoryItemView(
            item.Id,
            item.Sku,
            item.Name,
            item.Type.ToString(),
            item.CompatiblePrinterModels,

            // Items written before the units had codes read as the code (T-323).
            InventoryUnitNames.Normalize(item.Unit),
            item.SheetsPerUnit,
            item.MinStock,
            item.Stock,
            item.Location,
            item.IsActive,
            item.IsLow,
            item.LowSince);
    }
}

public sealed record InventoryMovementView(
    Guid Id, Guid ItemId, string? ItemName, string Type, decimal Quantity, decimal StockAfter, Guid? KioskId, Guid? TaskId, Guid? By, DateTime At, string? Note);

/// <summary>What a printer supply is, read from what the printer calls it.</summary>
public static class PrinterSupplies
{
    /// <summary>A toner, a drum, a waste box or something else, from the type and the name the printer reports.</summary>
    public static SupplyResource ResourceOf(PrinterSupplyLevel supply)
    {
        ArgumentNullException.ThrowIfNull(supply);

        var text = $"{supply.Type} {supply.Name}";

        return text.Contains("waste", StringComparison.OrdinalIgnoreCase) ? SupplyResource.WasteToner
            : text.Contains("drum", StringComparison.OrdinalIgnoreCase) || text.Contains("opc", StringComparison.OrdinalIgnoreCase) ? SupplyResource.Drum
            : text.Contains("toner", StringComparison.OrdinalIgnoreCase) || text.Contains("ink", StringComparison.OrdinalIgnoreCase) ? SupplyResource.Toner
            : SupplyResource.Other;
    }

    /// <summary>
    /// Which one of its kind: the colour when the printer says it in a word
    /// ("black"), otherwise the supply's own name - a colour given as a code
    /// ("#000000") tells an operator nothing.
    /// </summary>
    public static string VariantOf(PrinterSupplyLevel supply)
    {
        ArgumentNullException.ThrowIfNull(supply);

        return !string.IsNullOrWhiteSpace(supply.Color) && supply.Color.Trim().All(char.IsLetter)
            ? supply.Color.Trim()
            : supply.Name.Trim();
    }
}
