using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Features;
using NeoPrint.Application.Files;
using NeoPrint.Application.Sharing;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Sharing a file with another user (plan sections 6 and 8.2; docs/DECISIONS.md
/// 2026-10-05): sent to their <c>NP-XXXXXX</c> id, previewed in their
/// "Alınanlar", and theirs only once they save a copy.
/// </summary>
[ApiController]
[Route("api/shares")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class SharesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public SharesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Sends the file as it is now. A personal document needs <c>confirmSensitive: true</c>
    /// (<c>400 shares.sensitiveNeedsConfirmation</c> otherwise, for the app to show its warning).
    /// Sending the same file to the same person again, before they saved it, is the same share.
    /// </summary>
    [HttpPost]
    [RequireFeature(FeatureKeys.FileSharing)]
    public async Task<ActionResult<ShareView>> Send([FromBody] SendShareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new SendShareCommand(UserId, request.FileId, request.ToPublicId, request.Message, request.ConfirmSensitive),
            cancellationToken));
    }

    /// <summary>"Göndərilənlər", newest first.</summary>
    [HttpGet("sent")]
    public async Task<ActionResult<ShareList>> Sent(CancellationToken cancellationToken)
        => Ok(new ShareList(await _sender.Send(new ListSentSharesQuery(UserId), cancellationToken)));

    /// <summary>"Alınanlar", newest first.</summary>
    [HttpGet("received")]
    public async Task<ActionResult<ShareList>> Received(CancellationToken cancellationToken)
        => Ok(new ShareList(await _sender.Send(new ListReceivedSharesQuery(UserId), cancellationToken)));

    /// <summary>
    /// "Fayllarıma saxla": the recipient's own encrypted copy, in their quota. Saving again
    /// returns the same copy while it exists. <c>409 shares.fileGone</c> / <c>shares.fileChanged</c>
    /// when the sender deleted or changed the file.
    /// </summary>
    [HttpPost("{id:guid}/save")]
    public async Task<ActionResult<UserFileResult>> Save(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SaveShareCommand(UserId, id), cancellationToken));

    /// <summary>A page of a received file, before it is saved (as <c>GET files/{id}/pages/{page}.png</c>).</summary>
    [HttpGet("{id:guid}/pages/{page:int}.png")]
    public async Task<IActionResult> PageImage(Guid id, int page, [FromQuery] int? width, CancellationToken cancellationToken)
    {
        var image = await _sender.Send(
            new GetSharePageImageQuery(UserId, id, page, width, Request.Headers.IfNoneMatch.ToString()),
            cancellationToken);

        Response.Headers.ETag = image.ETag;
        Response.Headers.CacheControl = "private, max-age=86400";

        return image.NotModified
            ? StatusCode(StatusCodes.Status304NotModified)
            : File(image.Png!, "image/png");
    }

    /// <summary>The QR others scan to send this user a file: <c>neoprint://share/NP-XXXXXX</c>.</summary>
    [HttpGet("qr.png")]
    public async Task<IActionResult> Qr(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(await _sender.Send(new GetMyShareQrQuery(UserId), cancellationToken), "image/png");
    }
}

/// <param name="ToPublicId">The recipient's id, <c>NP-XXXXXX</c>.</param>
/// <param name="Message">An optional note, at most 200 characters.</param>
public sealed record SendShareRequest(Guid FileId, string? ToPublicId, string? Message, bool ConfirmSensitive);

public sealed record ShareList(IReadOnlyList<ShareView> Items);
