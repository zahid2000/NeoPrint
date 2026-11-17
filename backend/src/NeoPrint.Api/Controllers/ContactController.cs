using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Support;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>The app's "Əlaqə" (T-438): the active ways to reach the company.</summary>
[ApiController]
[Route("api/contact")]
public sealed class ContactController : ControllerBase
{
    private readonly ISender _sender;

    public ContactController(ISender sender) => _sender = sender;

    /// <summary>Anonymous: a customer who cannot sign in still finds the hotline. Public settings only; cached for a minute.</summary>
    [HttpGet]
    [AllowAnonymous]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<IReadOnlyList<PublicContactChannel>>> Get(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPublicContactQuery(), cancellationToken));
}

/// <summary>The channels in the panel (T-438): set, and switched on or off.</summary>
[ApiController]
[Route("api/admin/contact-channels")]
public sealed class AdminContactController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminContactController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    [HttpGet]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<ContactChannelView>>> Get(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetContactChannelsQuery(), cancellationToken));

    [HttpPut]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<ContactChannelView>>> Set([FromBody] ContactChannelsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        return Ok(await _sender.Send(new SetContactChannelsCommand(input.Channels ?? [], AdminId, _currentUser.Ip), cancellationToken));
    }
}

public sealed record ContactChannelsInput(IReadOnlyList<ContactChannelView>? Channels);
