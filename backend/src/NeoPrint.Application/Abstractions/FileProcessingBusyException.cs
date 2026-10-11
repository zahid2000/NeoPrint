using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// The service that opens users' documents is running, but it has as much to
/// do as it is allowed to take on: every process slot is in use and the queue
/// behind them is full, or this request waited in it too long
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// Answered with 503 and <c>Retry-After</c>: nothing is wrong with the request
/// or the file, and the same request works a few seconds later. Kept apart
/// from <see cref="FileProcessingUnavailableException"/> so the user is told
/// "busy, try in a moment" rather than "not working".
/// </remarks>
public sealed class FileProcessingBusyException : Exception
{
    public const string MessageKey = "files.processingBusy";

    public FileProcessingBusyException(int retryAfterSeconds)
        : base("The document processing service is at its limit.")
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>When trying again is worth it.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>What to tell the user.</summary>
    public static UserMessage Text => new(MessageKey);
}
