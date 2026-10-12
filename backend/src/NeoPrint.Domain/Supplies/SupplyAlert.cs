using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Supplies;

/// <summary>How far below its rule a level is.</summary>
public enum SupplyAlertLevel
{
    Warning = 0,
    Critical = 1,
}

/// <summary>
/// A kiosk's supply that is below its rule (plan section 2.15,
/// <c>supply_alerts</c>): paper in a tray, a toner, a drum.
/// </summary>
/// <remarks>
/// One alert per kiosk and supply while it lasts - <see cref="Key"/> is unique
/// among the active ones - so a kiosk that stays below its threshold is one
/// alert, not one per evaluation. It is told about once per level: when it is
/// raised, when the level changes, and when it clears.
/// </remarks>
public sealed class SupplyAlert : Entity
{
    public Guid KioskId { get; set; }

    /// <summary>The rule that judged the level; kept for the panel, not followed afterwards.</summary>
    public Guid RuleId { get; set; }

    public SupplyResource Resource { get; set; }

    /// <summary>Which toner or which supply: its colour or its name. Null for paper.</summary>
    public string? Variant { get; set; }

    public SupplyUnit Unit { get; set; }

    public SupplyAlertLevel Level { get; private set; }

    /// <summary>The last reading: sheets or percent, as <see cref="Unit"/> says.</summary>
    public decimal Value { get; private set; }

    /// <summary>Not cleared yet. Stored for the unique index: one active alert per <see cref="Key"/>.</summary>
    public bool Active { get; private set; } = true;

    /// <summary>Since when it has been at <see cref="Level"/>.</summary>
    public DateTime LevelSince { get; private set; }

    public DateTime? ClearedAt { get; private set; }

    /// <summary>The service task opened for it, when its rule asks for one.</summary>
    public Guid? ServiceTaskId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>What makes a kiosk's supply one of a kind: <c>{kiosk}:PaperA4</c>, <c>{kiosk}:Toner:black</c>.</summary>
    public static string KeyOf(Guid kioskId, SupplyResource resource, string? variant)
        => string.IsNullOrWhiteSpace(variant)
            ? $"{kioskId:N}:{resource}"
            : $"{kioskId:N}:{resource}:{variant.Trim().ToLowerInvariant()}";

    public static SupplyAlert Raise(
        Guid kioskId, SupplyAlertRule rule, string? variant, SupplyAlertLevel level, decimal value, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new SupplyAlert
        {
            KioskId = kioskId,
            RuleId = rule.Id,
            Resource = rule.Resource,
            Variant = string.IsNullOrWhiteSpace(variant) ? null : variant.Trim(),
            Unit = rule.Unit,
            Level = level,
            Value = value,
            LevelSince = now,
            Key = KeyOf(kioskId, rule.Resource, variant),
            CreatedAt = now,
        };
    }

    /// <summary>
    /// A new reading that is still below the rule.
    /// </summary>
    /// <returns>True when the level changed - the only time anybody is told again.</returns>
    public bool Observe(SupplyAlertLevel level, decimal value, DateTime now)
    {
        Value = value;

        if (!Active || level == Level)
        {
            return false;
        }

        Level = level;
        LevelSince = now;

        return true;
    }

    /// <summary>The level is back above the rule: the alert is over.</summary>
    public void Clear(decimal value, DateTime now)
    {
        if (!Active)
        {
            return;
        }

        Value = value;
        Active = false;
        ClearedAt = now;
    }
}
