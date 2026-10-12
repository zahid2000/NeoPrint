using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Supplies;

/// <summary>What a rule watches.</summary>
public enum SupplyResource
{
    PaperA4 = 0,
    PaperA3 = 1,
    Toner = 2,
    Drum = 3,
    WasteToner = 4,
    Other = 5,
}

/// <summary>How a threshold is expressed.</summary>
public enum SupplyUnit
{
    Sheets = 0,
    Percent = 1,
}

/// <summary>Where an alert is delivered.</summary>
[Flags]
public enum AlertChannels
{
    None = 0,
    AdminPanel = 1,
    OperatorPanel = 2,
    Email = 4,
    Telegram = 8,
}

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// A consumable threshold rule (plan section 2.15, <c>supply_alert_rules</c>).
/// </summary>
/// <remarks>
/// A rule scoped to a kiosk replaces the global rule for that kiosk. An alert
/// is raised once per level change, so a kiosk sitting below a threshold does
/// not notify repeatedly.
/// </remarks>
public sealed class SupplyAlertRule : Entity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Empty means the rule is global.</summary>
    public List<Guid> KioskIds { get; set; } = [];

    public SupplyResource Resource { get; set; }

    /// <summary>Toner colour, when the resource is toner.</summary>
    public string? ResourceVariant { get; set; }

    public SupplyUnit Unit { get; set; }

    public decimal WarningThreshold { get; set; }

    public decimal CriticalThreshold { get; set; }

    public AlertChannels Channels { get; set; } = AlertChannels.AdminPanel | AlertChannels.OperatorPanel;

    /// <summary>Whether crossing the threshold also opens a service task.</summary>
    public bool CreateServiceTask { get; set; }

    public TaskPriority TaskPriority { get; set; } = TaskPriority.Normal;

    public bool IsActive { get; set; } = true;

    public bool IsGlobal => KioskIds.Count == 0;

    /// <summary>Whether the rule is about paper in a tray - the one supply counted in sheets.</summary>
    public bool IsPaper => Resource is SupplyResource.PaperA4 or SupplyResource.PaperA3;

    /// <summary>
    /// Where a reading stands against the rule: below the critical threshold,
    /// below the warning one, or neither ("A4 qalıq 200-dən aşağı", plan
    /// section 9.9 - a tray with exactly 200 sheets is not below 200).
    /// </summary>
    public SupplyAlertLevel? LevelOf(decimal value)
        => value < CriticalThreshold ? SupplyAlertLevel.Critical
            : value < WarningThreshold ? SupplyAlertLevel.Warning
            : null;

    /// <summary>Whether the rule watches this supply of a printer: its kind and, when the rule names one, its colour or name.</summary>
    public bool Watches(SupplyResource resource, string? variant)
        => Resource == resource
           && (string.IsNullOrWhiteSpace(ResourceVariant)
               || string.Equals(ResourceVariant.Trim(), variant?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a rule has to be before it is saved.
    /// </summary>
    /// <exception cref="DomainException">The rule could never fire, or could not be told apart.</exception>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 100)
        {
            throw new DomainException("supplyRules.nameInvalid");
        }

        // Only paper is counted; everything else is what the printer reports - a percentage.
        if (!IsPaper && Unit != SupplyUnit.Percent)
        {
            throw new DomainException("supplyRules.unitPercentOnly");
        }

        if (CriticalThreshold <= 0 || WarningThreshold <= CriticalThreshold)
        {
            throw new DomainException("supplyRules.thresholdsOrder");
        }

        if (Unit == SupplyUnit.Percent && WarningThreshold > 100)
        {
            throw new DomainException("supplyRules.percentTooHigh");
        }

        if (Unit == SupplyUnit.Sheets && (WarningThreshold != decimal.Truncate(WarningThreshold) || CriticalThreshold != decimal.Truncate(CriticalThreshold)))
        {
            throw new DomainException("supplyRules.sheetsWhole");
        }

        if (IsPaper && !string.IsNullOrWhiteSpace(ResourceVariant))
        {
            throw new DomainException("supplyRules.paperHasNoVariant");
        }

        if (Channels == AlertChannels.None)
        {
            throw new DomainException("supplyRules.channelRequired");
        }
    }
}
