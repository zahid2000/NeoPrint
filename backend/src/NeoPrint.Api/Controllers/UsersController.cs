using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.UserQr;

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

    /// <summary>A code that lets a kiosk open the account's files must not sit in a cache.</summary>
    private void NoStore() => Response.Headers.CacheControl = "no-store";
}
