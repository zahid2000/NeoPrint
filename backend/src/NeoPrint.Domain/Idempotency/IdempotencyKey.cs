using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Idempotency;

/// <summary>Where a request with an idempotency key has got to.</summary>
public enum IdempotencyStatus
{
    /// <summary>The first request is still running.</summary>
    InProgress = 0,

    /// <summary>It finished, and the response was kept to replay.</summary>
    Completed = 1,
}

/// <summary>
/// A request an operation has already handled (<c>docs/IDEMPOTENCY.md</c>,
/// <c>idempotency_keys</c>).
/// </summary>
/// <remarks>
/// A caller that does not hear back cannot tell a lost response from a lost
/// request, so it retries. Without this the retry charges the card a second
/// time. With it, the second request gets the first one's answer.
///
/// Kept for 24 hours (plan section 5): long enough to cover a client retrying
/// after an outage, short enough that the collection does not grow without
/// bound.
/// </remarks>
public sealed class IdempotencyKey : Entity
{
    /// <summary>How long a record is kept before the TTL index removes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The key the caller sent.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Who sent it. Part of what makes a key unique, so one caller's key can
    /// never replay another caller's response.
    /// </summary>
    public string ActorKey { get; set; } = string.Empty;

    /// <summary>The operation it belongs to, as method and path.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// A hash of the request body.
    /// </summary>
    /// <remarks>
    /// The body itself is not stored: it carries payment details, file names
    /// and personal data, and this collection is not where those belong
    /// (plan section 2.13). The hash is enough to tell "the same request
    /// again" from "a different request reusing a key", which the document
    /// says must be a conflict.
    /// </remarks>
    public string RequestHash { get; set; } = string.Empty;

    public IdempotencyStatus Status { get; set; } = IdempotencyStatus.InProgress;

    /// <summary>The status code the first request answered with.</summary>
    public int? ResponseStatusCode { get; set; }

    /// <summary>
    /// The response body, replayed to a retry.
    /// </summary>
    /// <remarks>
    /// Only for responses the endpoint itself produced - an identifier, a
    /// receipt number. Nothing is stored for a failure, because a failed
    /// request is one worth genuinely retrying.
    /// </remarks>
    public string? ResponseBody { get; set; }

    public string? ResponseContentType { get; set; }

    /// <summary>Ties the record to the request that created it.</summary>
    public string? CorrelationId { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Whether a retry can be answered from this record.</summary>
    public bool HasReplayableResponse
        => Status == IdempotencyStatus.Completed && ResponseStatusCode is not null;
}
