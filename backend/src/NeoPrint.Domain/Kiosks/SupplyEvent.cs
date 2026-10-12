namespace NeoPrint.Domain.Kiosks;

/// <summary>How a tray is refilled (plan section 2.15, "Kağız doldur").</summary>
public enum PaperRefillMode
{
    /// <summary>This many sheets were put in.</summary>
    Add = 0,

    /// <summary>The tray was filled to its capacity.</summary>
    Full = 1,

    /// <summary>The tray was counted: this is how many there are.</summary>
    Exact = 2,
}

/// <summary>What moved a kiosk's paper counter (plan section 2.15, <c>supply_events</c>).</summary>
public enum SupplyEventType
{
    /// <summary>A job's sheets came out.</summary>
    JobPrinted = 0,

    /// <summary>A test page came out.</summary>
    TestPrint = 1,

    /// <summary>Sheets were added to the tray.</summary>
    Refill = 2,

    /// <summary>Somebody counted the tray and set the number.</summary>
    Recount = 3,

    /// <summary>A cartridge or a drum was changed: no count moves, the row says when and by whom.</summary>
    Replaced = 4,
}

/// <summary>
/// One movement of a kiosk's paper counter (plan section 2.15).
/// </summary>
/// <remarks>
/// <para>
/// The counter on the kiosk is the materialised value; these rows are how it
/// got there, so a counter that disagrees with the tray can be explained - the
/// same relation a ledger has with a balance.
/// </para>
/// <para>
/// A job or a test page moves the counter once: <see cref="DedupKey"/> is
/// unique, and the row is written in the same transaction as the counter.
/// </para>
/// </remarks>
public sealed class SupplyEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KioskId { get; set; }

    /// <summary>"PaperA4" or "PaperA3"; for a replacement the supply, "Toner:black".</summary>
    public string Resource { get; set; } = string.Empty;

    public SupplyEventType Type { get; set; }

    /// <summary>Sheets added (positive) or used (negative).</summary>
    public int Delta { get; set; }

    public int Before { get; set; }

    public int After { get; set; }

    public Guid? JobId { get; set; }

    public Guid? CommandId { get; set; }

    /// <summary>The administrator who refilled or recounted; null for printing.</summary>
    public Guid? By { get; set; }

    public DateTime At { get; set; }

    /// <summary>What makes the movement one of a kind: <c>job:{id}</c> (<c>job:{id}:{run}</c> from its second run on), <c>command:{id}</c>, or the row itself.</summary>
    public string DedupKey { get; set; } = string.Empty;

    /// <summary>The paper size's resource name.</summary>
    public static string PaperResource(string paper) => "Paper" + paper;
}
