using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Idempotency;

/// <summary>
/// A callback an external provider has already sent us
/// (<c>docs/IDEMPOTENCY.md</c>, <c>provider_events</c>).
/// </summary>
/// <remarks>
/// Payment providers resend a webhook until they get a 2xx, and they resend it
/// anyway when they are not sure. Every one of those deliveries describes the
/// same event, and acting on it twice is a double refund or a second fiscal
/// receipt.
///
/// The guard is a unique index on the provider and its own event id, so the
/// database refuses the duplicate rather than the code remembering to check.
/// Nothing here is a payment secret: an id, a type and a hash
/// (plan section 2.13).
/// </remarks>
public sealed class ProviderEvent : Entity
{
    /// <summary>Which provider sent it, for example <c>MockBank</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's own id for the event. Unique per provider.</summary>
    public string ProviderEventId { get; set; } = string.Empty;

    /// <summary>What the provider called it, for example <c>payment.succeeded</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// A hash of the payload.
    /// </summary>
    /// <remarks>
    /// The payload is not stored: it carries payment details. The hash makes it
    /// possible to notice a provider reusing an event id for different content,
    /// which is worth an incident rather than a silent accept.
    /// </remarks>
    public string PayloadHash { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ties the callback to what it caused.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The entity the callback acted on, once it is known.</summary>
    public string? EntityType { get; set; }

    public Guid? EntityId { get; set; }
}
