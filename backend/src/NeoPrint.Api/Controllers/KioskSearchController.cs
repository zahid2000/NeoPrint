using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Kiosks;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Kiosks as a customer finds them: the app's kiosks tab, list and map
/// (plan sections 6 and 8.2, step 5).
/// </summary>
/// <remarks>
/// A signed-in app user only. Nothing here is a secret - where a kiosk stands,
/// what it costs - but the whole fleet is not handed to anyone who asks either.
/// Only kiosks a customer can go to are found; the others answer 404.
/// </remarks>
[ApiController]
[Route("api/kiosks")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class KioskSearchController : ControllerBase
{
    private readonly ISender _sender;

    public KioskSearchController(ISender sender) => _sender = sender;

    /// <summary>By name, code or id; nearest first when a location is given.</summary>
    [HttpGet]
    public async Task<ActionResult<KioskSearchResult>> Search(
        [FromQuery] string? search, [FromQuery] double? lat, [FromQuery] double? lng, [FromQuery] double? radiusKm, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SearchKiosksQuery(search, lat, lng, radiusKm), cancellationToken));

    /// <summary>Status, capabilities, prices, queue length and paper (plan section 8.2, kiosk detail).</summary>
    [HttpGet("{idOrCode}")]
    public async Task<ActionResult<PublicKioskDetail>> Get(string idOrCode, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPublicKioskQuery(idOrCode), cancellationToken));

    /// <summary>How many jobs are ahead - a count, never whose.</summary>
    [HttpGet("{id:guid}/queue")]
    public async Task<ActionResult<PublicKioskQueue>> Queue(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPublicKioskQueueQuery(id), cancellationToken));
}
