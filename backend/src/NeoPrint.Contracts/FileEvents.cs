namespace NeoPrint.Contracts;

/// <summary>
/// A file was stored and is waiting to be converted to PDF.
/// </summary>
/// <remarks>
/// <para>
/// Published once the document exists, so the upload returns as soon as the
/// bytes are safe rather than holding the request open for a conversion that
/// can take a minute (plan section 2.2).
/// </para>
/// <para>
/// Carries the id and nothing else. The consumer reads the current state from
/// the database: a message that travelled with a copy of the file's metadata
/// would be acting on what was true when it was published, and it may be
/// redelivered long afterwards.
/// </para>
/// </remarks>
/// <param name="FileId">The file to convert.</param>
/// <param name="CorrelationId">
/// The request that caused it, so the conversion's log lines join the
/// upload's (<c>docs/OBSERVABILITY.md</c>).
/// </param>
public sealed record FileUploaded(Guid FileId, string? CorrelationId);
