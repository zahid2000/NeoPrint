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
/// <remarks>
/// The message reaches the caller as the problem detail, so it names the kind
/// of thing that was not found and never the value that was looked up. Some of
/// those values are short-lived secrets - a device pairing code, an enrollment
/// token - and echoing one back would put it in response bodies and proxy logs
/// (docs/SECURITY_THREAT_MODEL.md).
/// </remarks>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string entity)
        : base($"{entity} was not found.")
    {
    }
}
