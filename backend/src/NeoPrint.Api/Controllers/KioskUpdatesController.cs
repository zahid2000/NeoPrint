using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.Releases;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Releases;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Updates from the kiosk's side (plan section 6 "Agent (Kiosk token)",
/// 7.12; T-317): what to run, the package itself, and how the update went.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = AuthorizationPolicies.Kiosk)]
public sealed class KioskUpdatesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public KioskUpdatesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// What this kiosk should run, component by component, with the signed
    /// manifest and signature to check each package against, and the packages
    /// withdrawn since their release.
    /// </summary>
    [HttpGet("kiosks/me/update")]
    public async Task<ActionResult<KioskUpdateView>> Update(CancellationToken cancellationToken)
        => _currentUser.DeviceId is { } deviceId
            ? Ok(await _sender.Send(new GetKioskUpdateQuery(deviceId), cancellationToken))
            : Forbid();

    /// <summary>A package this kiosk is offered right now; any other is "not found".</summary>
    [HttpGet("releases/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        var download = await _sender.Send(new OpenReleasePackageQuery(deviceId, id), cancellationToken);
        Response.Headers.AcceptRanges = "bytes";

        // A download cut off (power, network) goes on from where it stopped
        // (T-321): "bytes=N-" only - the kiosk never asks for anything else.
        // The store hands out a decrypting stream that cannot seek, so the
        // first N bytes are read past.
        if (Request.Headers.Range.ToString() is { Length: > 0 } range)
        {
            await using var content = download.Content;
            var match = System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d+)-$");
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var from) || from >= download.Size)
            {
                Response.Headers.ContentRange = $"bytes */{download.Size}";
                return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            await SkipAsync(content, from, cancellationToken);
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.ContentType = "application/octet-stream";
            Response.ContentLength = download.Size - from;
            Response.Headers.ContentRange = $"bytes {from}-{download.Size - 1}/{download.Size}";
            await content.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }

        return File(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
    }

    /// <summary>
    /// The kiosk's logs on an UploadLogs command (T-321): a gzip file of text
    /// lines, at most <c>Logs.MaxUploadMb</c>; cleaned of personal data again
    /// here and kept encrypted.
    /// </summary>
    [HttpPost("kiosks/me/logs")]
    [RequestSizeLimit(110L * 1024 * 1024)]
    public async Task<ActionResult<KioskLogView>> Logs([FromQuery] Guid? commandId, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        return Ok(await _sender.Send(new UploadKioskLogCommand(deviceId, Request.Body, commandId, _currentUser.Ip), cancellationToken));
    }

    private static async Task SkipAsync(Stream content, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            count -= read;
        }
    }

    /// <summary>Where an update of one component stands on this kiosk.</summary>
    [HttpPost("kiosks/me/update-status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request, CancellationToken cancellationToken)
    {
        if (_currentUser.DeviceId is not { } deviceId)
        {
            return Forbid();
        }

        await _sender.Send(
            new ReportKioskUpdateCommand(deviceId, request.Component, request.Version ?? string.Empty, request.Status, request.MessageKey, _currentUser.Ip),
            cancellationToken);
        return NoContent();
    }
}

/// <param name="MessageKey">What happened, as a message key the backend knows (<c>updates.signatureRefused</c>).</param>
public sealed record UpdateStatusRequest(ReleaseComponent Component, string? Version, KioskUpdateStatus Status, string? MessageKey);
