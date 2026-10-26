using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Supplies;

public enum InventoryItemType
{
    PaperA4Ream = 0,
    PaperA3Ream = 1,
    Toner = 2,
    Drum = 3,
    Other = 4,
}

/// <summary>Why the warehouse's count of an item moved.</summary>
public enum InventoryMovementType
{
    /// <summary>Bought: the count goes up.</summary>
    Purchase = 0,

    /// <summary>Handed to an operator to carry: the count goes down.</summary>
    IssueToOperator = 1,

    /// <summary>Taken for a kiosk, usually on finishing a service task: the count goes down.</summary>
    UsedAtKiosk = 2,

    /// <summary>The shelf was counted: the difference, either way.</summary>
    Adjustment = 3,

    /// <summary>Damaged, expired, lost: the count goes down.</summary>
    WriteOff = 4,
}

/// <summary>
/// How a warehouse item is counted (T-323): one of a few known units, kept as
/// a code the panel writes out in the reader's language, or the warehouse's own
/// word. Never a number - "500" in the unit field is a quantity typed in the
/// wrong place, and the table then read "0 500" as if it were "0.500".
/// </summary>
public static class InventoryUnits
{
    public const string Ream = "ream";
    public const string Piece = "piece";
    public const string Box = "box";
    public const string Pack = "pack";
    public const string Roll = "roll";

    public const int MaxLength = 30;

    public static readonly IReadOnlyList<string> Known = [Ream, Piece, Box, Pack, Roll];

    /// <summary>A unit is a word: it has a letter and no digit.</summary>
    public static bool IsValid(string unit)
        => !string.IsNullOrWhiteSpace(unit)
           && unit.Length <= MaxLength
           && unit.Any(char.IsLetter)
           && !unit.Any(char.IsDigit);
}

/// <summary>
/// Something kept in the warehouse for the kiosks (plan section 2.15,
/// <c>inventory_items</c>): reams of paper, toner cartridges, drums.
/// </summary>
/// <remarks>
/// <see cref="Stock"/> is the materialised count; <c>inventory_movements</c>
/// is how it got there, and the count only changes through
/// <see cref="Move"/> - the same relation a kiosk's paper counter has with
/// <c>supply_events</c>.
/// </remarks>
public sealed class InventoryItem : Entity
{
    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public InventoryItemType Type { get; set; }

    /// <summary>The printer models a cartridge or a drum fits; empty for what fits any.</summary>
    public List<string> CompatiblePrinterModels { get; set; } = [];

    /// <summary>What one of it is called: a code of <see cref="InventoryUnits"/>, or the warehouse's own word.</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>How many sheets one unit holds, for paper: a ream is 500.</summary>
    public int? SheetsPerUnit { get; set; }

    public decimal MinStock { get; set; }

    public decimal Stock { get; private set; }

    public string Location { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Since when the count has been below <see cref="MinStock"/>; null while it is not.</summary>
    public DateTime? LowSince { get; private set; }

    public bool IsLow => IsActive && Stock < MinStock;

    /// <summary>Whether the item fits a printer: one that names no models fits all.</summary>
    public bool Fits(string? printerModel)
        => CompatiblePrinterModels.Count == 0
           || (!string.IsNullOrWhiteSpace(printerModel)
               && CompatiblePrinterModels.Any(model => printerModel.Contains(model.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Moves the count and says how.
    /// </summary>
    /// <param name="quantity">
    /// How many units, always positive - except for an
    /// <see cref="InventoryMovementType.Adjustment"/>, where the sign is the
    /// direction.
    /// </param>
    /// <exception cref="DomainException">Nothing to move, or more than the shelf holds.</exception>
    public InventoryMovement Move(
        InventoryMovementType type,
        decimal quantity,
        Guid? by,
        DateTime now,
        Guid? kioskId = null,
        Guid? taskId = null,
        string? note = null)
    {
        if (quantity == 0 || (type != InventoryMovementType.Adjustment && quantity < 0))
        {
            throw new DomainException("inventory.quantityInvalid");
        }

        var delta = type switch
        {
            InventoryMovementType.Purchase => quantity,
            InventoryMovementType.Adjustment => quantity,
            _ => -quantity,
        };

        if (Stock + delta < 0)
        {
            throw new DomainException(UserMessage.Of("inventory.notEnough", ("item", Name), ("stock", Stock), ("unit", Unit)));
        }

        Stock += delta;
        LowSince = IsLow ? LowSince ?? now : null;

        return new InventoryMovement
        {
            ItemId = Id,
            Type = type,
            Quantity = delta,
            StockAfter = Stock,
            KioskId = kioskId,
            TaskId = taskId,
            By = by,
            At = now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
    }

    /// <summary>The minimum was changed: the item may be low now, or no longer.</summary>
    public void NoteMinimum(DateTime now) => LowSince = IsLow ? LowSince ?? now : null;

    /// <exception cref="DomainException">A field is missing or cannot be.</exception>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Sku) || Sku.Trim().Length > 60)
        {
            throw new DomainException("inventory.skuInvalid");
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 150)
        {
            throw new DomainException("inventory.nameInvalid");
        }

        if (!InventoryUnits.IsValid(Unit))
        {
            throw new DomainException("inventory.unitInvalid");
        }

        if (MinStock < 0)
        {
            throw new DomainException("inventory.minStockInvalid");
        }

        if (SheetsPerUnit is <= 0)
        {
            throw new DomainException("inventory.sheetsPerUnitInvalid");
        }
    }
}

/// <summary>One movement of an item's count (plan section 2.15, <c>inventory_movements</c>).</summary>
public sealed class InventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ItemId { get; set; }

    public InventoryMovementType Type { get; set; }

    /// <summary>Units added (positive) or taken (negative).</summary>
    public decimal Quantity { get; set; }

    public decimal StockAfter { get; set; }

    public Guid? KioskId { get; set; }

    public Guid? TaskId { get; set; }

    /// <summary>The administrator who recorded it.</summary>
    public Guid? By { get; set; }

    public DateTime At { get; set; }

    public string? Note { get; set; }
}
