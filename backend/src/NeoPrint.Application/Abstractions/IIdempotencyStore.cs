using NeoPrint.Domain.Idempotency;

namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Remembers requests that carried an <c>Idempotency-Key</c>
/// (<c>docs/IDEMPOTENCY.md</c>).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims a key for this request, or reports what happened to it before.
    /// </summary>
    /// <remarks>
    /// Claiming is a single atomic insert, so two requests arriving together
    /// cannot both believe they are the first.
    /// </remarks>
    Task<IdempotencyClaim> ClaimAsync(
        string key,
        string actorKey,
        string operation,
        string requestHash,
        string? correlationId,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the response, so a retry can be answered from it.</summary>
    Task CompleteAsync(
        Guid recordId,
        int statusCode,
        string? body,
        string? contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a key whose request failed.
    /// </summary>
    /// <remarks>
    /// A failed request is one worth genuinely retrying, and a caller must not
    /// be locked out of retrying it by the record of the attempt that failed.
    /// </remarks>
    Task ReleaseAsync(Guid recordId, CancellationToken cancellationToken = default);
}

/// <summary>What claiming an idempotency key produced.</summary>
public enum IdempotencyOutcome
{
    /// <summary>Nobody has used this key: carry on.</summary>
    Claimed = 0,

    /// <summary>The same request already finished; replay its response.</summary>
    Replay = 1,

    /// <summary>
    /// The same key is in flight. Answering would need the first request's
    /// result, which does not exist yet.
    /// </summary>
    InProgress = 2,

    /// <summary>The key was used for a different request body.</summary>
    Conflict = 3,
}

/// <param name="Outcome">What to do next.</param>
/// <param name="RecordId">The record to complete or release, when claimed.</param>
/// <param name="Existing">The earlier record, when there is one to replay.</param>
public sealed record IdempotencyClaim(
    IdempotencyOutcome Outcome,
    Guid RecordId = default,
    IdempotencyKey? Existing = null);

/// <summary>
/// Records provider callbacks, so the same one is never acted on twice
/// (<c>docs/IDEMPOTENCY.md</c>).
/// </summary>
public interface IProviderEventStore
{
    /// <summary>
    /// Records a callback, and says whether this is the first time it arrived.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the event is new and should be acted on; <c>false</c>
    /// when it has been seen before and must be ignored.
    /// </returns>
    Task<bool> TryRecordAsync(
        string provider,
        string providerEventId,
        string type,
        string payloadHash,
        string? correlationId,
        CancellationToken cancellationToken = default);
}
