using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Referrals;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The user's referral code and what it earned (plan sections 2.6, 6, 8.2).
/// Rewards themselves come from completed prints (<c>ReferralRewardConsumer</c>).
/// </summary>
[ApiController]
[Route("api/referrals")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class ReferralsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public ReferralsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// The code, its link (<c>https://neoprint.az/r/{code}</c>), how many registered with it,
    /// were rewarded and still wait for their first paid print, and the bonus pages earned.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<MyReferral>> Me(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetMyReferralQuery(UserId), cancellationToken));

    /// <summary>The link as a QR code.</summary>
    [HttpGet("me/qr.png")]
    public async Task<IActionResult> Qr(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(await _sender.Send(new GetMyReferralQrQuery(UserId), cancellationToken), "image/png");
    }
}
