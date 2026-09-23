namespace NeoPrint.Domain.Common;

/// <summary>
/// Base for every persisted aggregate.
/// </summary>
/// <remarks>
/// Identifiers are GUIDs and timestamps are UTC throughout (plan section 2.1:
/// the server stores UTC, the UI renders Asia/Baku).
/// </remarks>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
