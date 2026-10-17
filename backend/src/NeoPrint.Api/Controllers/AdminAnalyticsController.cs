using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Analytics;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Pilot hesabatı" (plan section 2.17): formats, the way through the
/// kiosk, payments, printing, supplies and incidents by Baku day. An account
/// with a kiosk scope reads its own kiosks only.
/// </summary>
[ApiController]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminAnalyticsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    /// <summary>Baku days, both included; at most a year. Today is worked out on the spot.</summary>
    [HttpGet("pilot")]
    [HasPermission(Permissions.AnalyticsView)]
    public async Task<ActionResult<PilotReportView>> Pilot(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] Guid? kioskId, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPilotReportQuery(AdminId, from, to, kioskId), cancellationToken));

    /// <summary>
    /// Works the days out again from the records - the account's own kiosks -
    /// as far back as the analytics events are kept. Safe to repeat.
    /// </summary>
    [HttpPost("pilot/rebuild")]
    [HasPermission(Permissions.AnalyticsView)]
    public async Task<ActionResult<PilotRebuildResult>> Rebuild([FromBody] PilotRebuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new RebuildPilotDaysCommand(AdminId, request.From, request.To, _currentUser.Ip), cancellationToken));
    }
}

public sealed record PilotRebuildRequest(DateOnly From, DateOnly To);
