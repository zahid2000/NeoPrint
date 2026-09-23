namespace NeoPrint.Domain.Common;

/// <summary>
/// A domain rule was broken - for example an illegal print job state
/// transition (plan section 5).
/// </summary>
/// <remarks>
/// Surfaces to the API as an RFC 7807 problem with status 409, so the message
/// is written to be shown to a caller and never contains internal detail.
/// </remarks>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The requested entity does not exist, or is not visible to the caller.</summary>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string entity, object id)
        : base($"{entity} '{id}' was not found.")
    {
    }
}
