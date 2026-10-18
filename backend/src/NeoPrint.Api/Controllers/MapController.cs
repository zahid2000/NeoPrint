using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Maps;

namespace NeoPrint.Api.Controllers;

/// <summary>The kiosks map's style and attribution, for the app and the panel (T-217).</summary>
[ApiController]
[Route("api/map")]
public sealed class MapController : ControllerBase
{
    private readonly ISender _sender;

    public MapController(ISender sender) => _sender = sender;

    /// <summary>
    /// Anonymous: the app shows the kiosks map before anyone signs in. Carries
    /// two public settings only. Cached for a minute.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<MapConfigView>> Config(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetMapConfigQuery(), cancellationToken));
}
