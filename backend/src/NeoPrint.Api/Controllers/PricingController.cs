using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Pricing;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The price of an order at a kiosk (plan section 6, <c>POST pricing/quote</c>).
/// </summary>
/// <remarks>
/// Any signed-in caller - the mobile app, a kiosk, the panel. Prices are not a
/// secret (the mobile kiosk page lists them), but the endpoint is not open to
/// anonymous callers either: it is a computation on request, and the global
/// rate limit is per identity.
/// </remarks>
[ApiController]
[Route("api/pricing")]
[Authorize]
public sealed class PricingController : ControllerBase
{
    private readonly ISender _sender;

    public PricingController(ISender sender) => _sender = sender;

    [HttpPost("quote")]
    [ProducesResponseType(typeof(QuoteResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<QuoteResponse>> Quote(
        [FromBody] QuoteRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new QuoteQuery(request.KioskId, request.Options, request.PromoCode, request.UseBonus),
            cancellationToken));
}

/// <param name="KioskId">The kiosk whose profile prices the order; null for the default profile.</param>
/// <param name="Options">Pages, copies, duplex, colour, paper, pages per sheet.</param>
/// <param name="PromoCode">Accepted; applied from step 4.</param>
/// <param name="UseBonus">Accepted; applied from step 4.</param>
public sealed record QuoteRequest(Guid? KioskId, PrintOptionsDto Options, string? PromoCode, bool UseBonus);
