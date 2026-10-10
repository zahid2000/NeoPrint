using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Referrals;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The referral programme in the admin panel (plan section 8A.3, "Referral"):
/// statistics, the top inviters and the history of invitations.
/// </summary>
[ApiController]
[Route("api/admin/referrals")]
public sealed class AdminReferralsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminReferralsController(ISender sender) => _sender = sender;

    /// <param name="status"><c>Waiting</c>, <c>Rewarded</c> or <c>Held</c>; nothing for every invitation.</param>
    [HttpGet]
    [HasPermission(Permissions.ReferralsView)]
    public async Task<ActionResult<AdminReferralPage>> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListAdminReferralsQuery(status, page, pageSize), cancellationToken));
}
