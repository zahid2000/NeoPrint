using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Referrals;

namespace NeoPrint.Application.Referrals;

/// <summary>
/// <c>GET admin/referrals</c> (plan sections 6, 8A.3 "Referral"): the
/// programme in numbers, the top inviters and the history of invitations.
/// </summary>
/// <param name="Status"><c>Waiting</c>, <c>Rewarded</c>, <c>Held</c>, or nothing for all.</param>
public sealed record ListAdminReferralsQuery(string? Status, int Page, int PageSize) : IRequest<AdminReferralPage>;

/// <summary>A user as the referral pages name them: the public id, never the email.</summary>
public sealed record ReferralParty(Guid Id, string PublicId);

public sealed record AdminTopInviter(ReferralParty Inviter, long Invited, long Rewarded, long BonusPages);

/// <param name="Hold"><c>None</c>, <c>SameDevice</c>, <c>MonthlyLimit</c> or <c>InviterInactive</c>.</param>
/// <param name="QualifiedAt">When the invitee's first paid print decided it; null while it waits.</param>
public sealed record AdminReferralRow(
    Guid Id,
    ReferralParty Inviter,
    ReferralParty Invitee,
    string Status,
    string Hold,
    DateTime CreatedAt,
    DateTime? QualifiedAt,
    int InviterBonusPages,
    int InviteeBonusPages);

public sealed record AdminReferralPage(
    ReferralProgrammeTotals Totals,
    IReadOnlyList<AdminTopInviter> Top,
    IReadOnlyList<AdminReferralRow> Items,
    long Total,
    int Page,
    int PageSize);

public sealed class AdminReferralHandlers : IRequestHandler<ListAdminReferralsQuery, AdminReferralPage>
{
    private const int MaxPageSize = 100;
    private const int TopCount = 10;

    private readonly IReferralRepository _referrals;
    private readonly IUserRepository _users;

    public AdminReferralHandlers(IReferralRepository referrals, IUserRepository users)
    {
        _referrals = referrals;
        _users = users;
    }

    public async Task<AdminReferralPage> Handle(ListAdminReferralsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ReferralFilter? filter = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            filter = Enum.TryParse<ReferralFilter>(request.Status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException([new ValidationError("status", new UserMessage("referrals.statusInvalid"))]);
        }

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var totals = await _referrals.ProgrammeTotalsAsync(cancellationToken).ConfigureAwait(false);
        var top = await _referrals.TopInvitersAsync(TopCount, cancellationToken).ConfigureAwait(false);
        var (items, total) = await _referrals.SearchAsync(filter, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        var parties = await PartiesAsync(
                _users,
                [.. top.Select(t => t.InviterId), .. items.SelectMany(r => new[] { r.InviterId, r.InviteeId })],
                cancellationToken)
            .ConfigureAwait(false);

        return new AdminReferralPage(
            totals,
            [.. top.Select(t => new AdminTopInviter(parties[t.InviterId], t.Invited, t.Rewarded, t.BonusPages))],
            [.. items.Select(r => Row(r, parties))],
            total,
            page,
            size);
    }

    internal static AdminReferralRow Row(Referral referral, IReadOnlyDictionary<Guid, ReferralParty> parties) => new(
        referral.Id,
        parties[referral.InviterId],
        parties[referral.InviteeId],
        referral.Status.ToString(),
        referral.Hold.ToString(),
        referral.CreatedAt,
        referral.QualifiedAt,
        referral.InviterBonusPages,
        referral.InviteeBonusPages);

    /// <summary>The public ids of these users; an account no longer there keeps its place with an empty id.</summary>
    internal static async Task<Dictionary<Guid, ReferralParty>> PartiesAsync(
        IUserRepository users, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var parties = new Dictionary<Guid, ReferralParty>();

        foreach (var id in ids.Distinct())
        {
            var user = await users.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
            parties[id] = new ReferralParty(id, user?.PublicId ?? string.Empty);
        }

        return parties;
    }
}
