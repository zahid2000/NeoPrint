namespace NeoPrint.Contracts;

/// <summary>
/// An invitation was rewarded (plan sections 2.6, 10: the invitee's first paid
/// print completed and both bonuses were written); the two users hear it.
/// </summary>
public sealed record ReferralQualified(
    Guid ReferralId, Guid InviterId, Guid InviteeId, int InviterBonusPages, int InviteeBonusPages, string? CorrelationId);
