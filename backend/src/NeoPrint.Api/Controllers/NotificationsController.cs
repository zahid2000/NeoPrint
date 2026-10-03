using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Notifications;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The user's notifications and the devices that get them as push (plan
/// sections 6 and 8.2; <c>docs/DECISIONS.md</c> T-146).
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class NotificationsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public NotificationsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Newest first, in the request's language; <c>unread</c> counts all of the
    /// user's unread. The next page: <c>?before={next}</c>.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<NotificationPage>> List(
        [FromQuery] DateTime? before, [FromQuery] int take = 20, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListNotificationsQuery(UserId, before?.ToUniversalTime(), take), cancellationToken));

    /// <summary>Opened in the app. Repeating it changes nothing.</summary>
    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<NotificationView>> Read(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new MarkNotificationReadCommand(UserId, id), cancellationToken));

    /// <summary>
    /// This device gets the user's push, written in <c>language</c> (the app's
    /// on that device). Sent at every start and whenever FCM renews the token;
    /// the same token again just refreshes it.
    /// </summary>
    [HttpPost("/api/users/me/fcm-token")]
    public async Task<IActionResult> RegisterDevice([FromBody] PushDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _sender.Send(
            new RegisterPushDeviceCommand(UserId, request.Token ?? string.Empty, request.Language, request.Platform), cancellationToken);
        return NoContent();
    }

    /// <summary>Signing out on this device: it stops getting the user's push.</summary>
    [HttpDelete("/api/users/me/fcm-token")]
    public async Task<IActionResult> RemoveDevice([FromBody] PushDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _sender.Send(new RemovePushDeviceCommand(UserId, request.Token ?? string.Empty), cancellationToken);
        return NoContent();
    }
}

public sealed record PushDeviceRequest(string? Token, string? Language, string? Platform);
