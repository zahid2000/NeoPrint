using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The kiosk camera in the panel (T-483): live and recorded, with
/// <c>camera.view</c> on a kiosk the account sees. Every look is audited.
/// </summary>
[ApiController]
[Route("api/admin/kiosks/{id:guid}/camera")]
public sealed class AdminCameraController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminCameraController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>The recorded minutes of a span (at most a day), and the newest request for it.</summary>
    [HttpGet("footage")]
    [HasPermission(Permissions.CameraView)]
    public async Task<ActionResult<CameraFootageView>> Footage(
        Guid id, [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListCameraFootageQuery(AdminId, id, from, to), cancellationToken));

    /// <summary>Asks the kiosk for the minutes of a span (at most an hour) it keeps on its own disk.</summary>
    [HttpPost("footage/requests")]
    [HasPermission(Permissions.CameraView)]
    public async Task<ActionResult<CameraRequestView>> RequestFootage(
        Guid id, [FromBody] CameraSpanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new RequestCameraFootageCommand(AdminId, id, request.From, request.To, _currentUser.Ip), cancellationToken));
    }

    /// <summary>One minute as MP4, opened for this look only.</summary>
    [HttpGet("footage/{minuteId:guid}")]
    [HasPermission(Permissions.CameraView)]
    public async Task<IActionResult> Minute(Guid id, Guid minuteId, CancellationToken cancellationToken)
    {
        var video = await _sender.Send(new OpenCameraMinuteQuery(AdminId, id, minuteId, _currentUser.Ip), cancellationToken);
        Response.Headers.CacheControl = "no-store";

        return File(video.Content, "video/mp4", video.FileName, enableRangeProcessing: true);
    }

    /// <summary>Starts watching live: the kiosk is told to send frames.</summary>
    [HttpPost("live")]
    [HasPermission(Permissions.CameraView)]
    public async Task<ActionResult<CameraLiveAnswer>> StartLive(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new StartCameraLiveCommand(AdminId, id, _currentUser.Ip), cancellationToken));

    /// <summary>The latest live frame (JPEG); 204 while none has come. Asking keeps the watch alive.</summary>
    [HttpGet("live/frame")]
    [HasPermission(Permissions.CameraView)]
    public async Task<IActionResult> Frame(Guid id, CancellationToken cancellationToken)
    {
        var frame = await _sender.Send(new GetCameraLiveFrameQuery(AdminId, id), cancellationToken);
        Response.Headers.CacheControl = "no-store";

        if (frame is null)
        {
            return NoContent();
        }

        Response.Headers["X-Frame-At"] = frame.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return File(frame.Jpeg, "image/jpeg");
    }

    /// <summary>Stops watching: the kiosk stops at its next frame.</summary>
    [HttpDelete("live")]
    [HasPermission(Permissions.CameraView)]
    public async Task<IActionResult> StopLive(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new StopCameraLiveCommand(AdminId, id), cancellationToken);
        return NoContent();
    }

    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");
}

/// <summary>A span of time, UTC.</summary>
public sealed record CameraSpanRequest(DateTime From, DateTime To);
