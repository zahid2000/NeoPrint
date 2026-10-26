using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Application.Releases;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The newest release, for a provisioning script on a kiosk that has no
/// identity yet (T-327): anonymous, rate limited per address, and showing
/// only a published production release - see <see cref="PublicReleaseHandlers"/>.
/// The script checks the signature itself with the keys its setup stick
/// carries; this endpoint is a place to fetch from, not a source of trust.
/// </summary>
[ApiController]
[Route("api/releases")]
[AllowAnonymous]
[EnableRateLimiting("release-download")]
public sealed class PublicReleasesController : ControllerBase
{
    private readonly ISender _sender;

    public PublicReleasesController(ISender sender) => _sender = sender;

    /// <summary>The newest release of a component (<c>app</c>, <c>agent</c>, <c>provisioning</c>) for a platform.</summary>
    [HttpGet("latest/{component}/{platform}")]
    public async Task<ActionResult<PublicReleaseView>> Latest(string component, string platform, [FromQuery] string? channel, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPublicReleaseQuery(component, platform, channel), cancellationToken));

    /// <summary>One of its files: <c>manifest</c>, <c>signature</c> or <c>package</c>.</summary>
    [HttpGet("{component}/{platform}/{version}/{file}")]
    public async Task<IActionResult> File(string component, string platform, string version, string file, CancellationToken cancellationToken)
    {
        var download = await _sender.Send(new OpenPublicReleaseFileQuery(component, platform, version, file), cancellationToken);
        return File(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }
}
