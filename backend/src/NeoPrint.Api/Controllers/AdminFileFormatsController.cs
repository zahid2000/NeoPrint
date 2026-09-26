using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The "Fayl formatları" page (plan sections 2.2, 6: <c>GET/PUT admin/files/formats</c>).
/// </summary>
[ApiController]
[Route("api/admin/files/formats")]
[HasPermission(Permissions.FormatsManage)]
public sealed class AdminFileFormatsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminFileFormatsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>The catalogue, which formats are on, and the default limits.</summary>
    [HttpGet]
    public async Task<ActionResult<FileFormatsResponse>> Get(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetFileFormatsQuery(), cancellationToken));

    [HttpPut]
    public async Task<ActionResult<FileFormatsResponse>> Update(
        [FromBody] UpdateFileFormatsRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdateFileFormatsCommand(
                request.Enabled ?? [],
                request.DefaultMaxSizeMb,
                request.DefaultMaxPages,
                request.UserQuotaGb,
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken));
}

public sealed record UpdateFileFormatsRequest(
    IReadOnlyList<string>? Enabled, int DefaultMaxSizeMb, int DefaultMaxPages, int UserQuotaGb);
