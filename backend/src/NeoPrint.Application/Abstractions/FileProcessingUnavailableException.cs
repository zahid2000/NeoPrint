using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// The service that opens users' documents - the isolated converter - is not
/// running, so a preview, an edit or a layout could not be made
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// Not a validation error: nothing is wrong with the request or the file, and
/// the same request works once the service is back. It is answered with 503 so
/// a client offers "try again" rather than "fix this".
/// </remarks>
public sealed class FileProcessingUnavailableException : Exception
{
    public const string MessageKey = "files.processingUnavailable";

    public FileProcessingUnavailableException(Exception? inner = null)
        : base("The document processing service is not running.", inner)
    {
    }

    /// <summary>What to tell the user.</summary>
    public static UserMessage Text => new(MessageKey);
}
