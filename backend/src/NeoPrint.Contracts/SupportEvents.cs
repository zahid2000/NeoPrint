namespace NeoPrint.Contracts;

/// <summary>
/// An admin answered a registered user's claim (T-385): the user is told, and
/// may carry the claim on. <paramref name="ReopenCount"/> makes each answer
/// its own notification.
/// </summary>
public sealed record SupportClaimAnswered(Guid ClaimId, Guid UserId, string? JobCode, int ReopenCount, string? CorrelationId);
