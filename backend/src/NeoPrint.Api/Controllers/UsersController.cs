using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.UserQr;
using NeoPrint.Application.Users;

namespace NeoPrint.Api.Controllers;

/// <summary>The signed-in user's own things (plan section 6, "Users").</summary>
[ApiController]
[Route("api/users/me")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class UsersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public UsersController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// The account's language (T-152): <c>{ "language": "az" | "en" | "ru" }</c>. Emails,
    /// pushes and server messages follow it on every device.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Update([FromBody] UpdateMeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _sender.Send(new ChangeMyLanguageCommand(UserId, request.Language), cancellationToken);
        return NoContent();
    }

    /// <summary>The personal QR now (plan section 2.9): good for <c>rotationSeconds</c>, then fetch a new one.</summary>
    [HttpGet("qr")]
    public async Task<ActionResult<MyUserQr>> Qr(CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await _sender.Send(new GetMyUserQrQuery(UserId), cancellationToken));
    }

    /// <summary>
    /// The personal QR as a PNG. When it stops being good is in the
    /// <c>X-Qr-Expires-At</c> header (ISO 8601, UTC), so one call gives both.
    /// </summary>
    [HttpGet("qr.png")]
    public async Task<IActionResult> QrPng(CancellationToken cancellationToken)
    {
        var image = await _sender.Send(new GetMyUserQrImageQuery(UserId), cancellationToken);

        NoStore();
        Response.Headers["X-Qr-Expires-At"] = image.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Qr-Rotation-Seconds"] = image.RotationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers.AccessControlExposeHeaders = "X-Qr-Expires-At, X-Qr-Rotation-Seconds";

        return File(image.Png, "image/png");
    }

    /// <summary>
    /// The "Çap kodu" (T-345): six digits typed at a kiosk instead of showing
    /// the QR; good for ten minutes and once.
    /// </summary>
    [HttpGet("print-code")]
    public async Task<ActionResult<MyPrintCode>> PrintCode(CancellationToken cancellationToken)
    {
        NoStore();
        return Ok(await _sender.Send(new GetMyPrintCodeQuery(UserId), cancellationToken));
    }

    private const long PhotoRequestBytes = ProfilePhotoHandlers.MaxBytes + (64 * 1024);

    /// <summary>
    /// The profile picture (T-396), one file field <c>photo</c>: JPEG, PNG or WebP
    /// as the phone took it, up to <see cref="ProfilePhotoHandlers.MaxBytes"/>.
    /// </summary>
    [HttpPut("photo")]
    [RequestSizeLimit(PhotoRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = PhotoRequestBytes)]
    public async Task<ActionResult<MyPhotoInfo>> SetPhoto(IFormFile? photo, CancellationToken cancellationToken)
    {
        byte[] bytes = [];
        if (photo is not null)
        {
            await using var stream = photo.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        return Ok(await _sender.Send(new SetMyPhotoCommand(UserId, bytes), cancellationToken));
    }

    /// <summary>The profile picture, for its owner only; 404 without one.</summary>
    [HttpGet("photo")]
    public async Task<IActionResult> Photo(CancellationToken cancellationToken)
    {
        if (await _sender.Send(new GetMyPhotoQuery(UserId), cancellationToken) is not { } photo)
        {
            return NotFound();
        }

        // Someone's face: not kept by a shared cache, and never sniffed as anything else.
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ETag = $"\"{photo.Version}\"";
        return File(photo.Bytes, photo.ContentType);
    }

    /// <summary>Takes the profile picture away.</summary>
    [HttpDelete("photo")]
    public async Task<IActionResult> RemovePhoto(CancellationToken cancellationToken)
    {
        await _sender.Send(new RemoveMyPhotoCommand(UserId), cancellationToken);
        return NoContent();
    }

    /// <summary>A code that lets a kiosk open the account's files must not sit in a cache.</summary>
    private void NoStore() => Response.Headers.CacheControl = "no-store";
}

public sealed record UpdateMeRequest(string? Language);
