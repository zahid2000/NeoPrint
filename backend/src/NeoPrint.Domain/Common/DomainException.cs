namespace NeoPrint.Domain.Common;

/// <summary>
/// A domain rule was broken - for example an illegal print job state
/// transition (plan section 5).
/// </summary>
/// <remarks>
/// Surfaces to the API as an RFC 7807 problem with status 409. What the caller
/// reads is <see cref="Text"/>, written out in the request's language; the
/// exception message is the key alone, so a log line never carries the
/// arguments (an email address, a file name).
/// </remarks>
public class DomainException : Exception
{
    public DomainException(string key)
        : this(new UserMessage(key))
    {
    }

    public DomainException(UserMessage text)
        : base(text.Key)
    {
        Text = text;
    }

    public DomainException(UserMessage text, Exception innerException)
        : base(text.Key, innerException)
    {
        Text = text;
    }

    /// <summary>What to tell the caller.</summary>
    public UserMessage Text { get; }
}

/// <summary>The requested entity does not exist, or is not visible to the caller.</summary>
/// <remarks>
/// The message names the kind of thing that was not found and never the value
/// that was looked up. Some of those values are short-lived secrets - a device
/// pairing code, an enrollment token - and echoing one back would put it in
/// response bodies and proxy logs (docs/SECURITY_THREAT_MODEL.md).
/// </remarks>
public sealed class NotFoundException : DomainException
{
    /// <param name="what">The kind of thing, as a catalog key segment: <c>device</c>, <c>file</c>.</param>
    public NotFoundException(string what)
        : base(new UserMessage($"notFound.{what}"))
    {
    }
}

/// <summary>
/// The document changed underneath us: somebody else saved it first
/// (<c>docs/IDEMPOTENCY.md</c>, concurrency).
/// </summary>
/// <remarks>
/// Not a rule violation but a race, so the operation can be retried against
/// what is actually stored. It surfaces as a 409, which is the same code a
/// caller would get for a rule violation, and that is the right answer here
/// too: try again.
/// </remarks>
public sealed class ConcurrencyException : DomainException
{
    public ConcurrencyException(string entityType, Guid id)
        : base(new UserMessage("common.concurrentChange"))
    {
        EntityType = entityType;
        EntityId = id;
    }

    public string EntityType { get; }

    public Guid EntityId { get; }
}
