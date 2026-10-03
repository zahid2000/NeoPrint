using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Schedules;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Scheduled printing (plan sections 6 and 9.5; <c>docs/DECISIONS.md</c> T-145):
/// a kiosk, a file and a time, paid in the app; when the time comes the app
/// asks "Çapa ver" or "Vaxtı dəyiş".
/// </summary>
[ApiController]
[Route("api/schedules")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class SchedulesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public SchedulesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// An order for <c>scheduledAt</c> (UTC; at least 15 minutes and at most <c>Schedule.MaxDaysAhead</c>
    /// days away) and its payment, paid through <c>payments/{paymentId}/pay-with-app</c>.
    /// <c>expectedAmount</c> is the price the app showed; <c>409 printJobs.priceChanged</c> when it changed since.
    /// </summary>
    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<ScheduleCreatedResponse>> Create(
        [FromBody] CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new CreateScheduleCommand(
                UserId,
                request.Kiosk ?? string.Empty,
                request.FileId,
                request.Options ?? PrintOptions.Default,
                request.ScheduledAt,
                request.ExpectedAmount),
            cancellationToken));
    }

    /// <summary>The user's scheduled prints, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<ScheduleList>> List(CancellationToken cancellationToken)
        => Ok(new ScheduleList(await _sender.Send(new ListMySchedulesQuery(UserId), cancellationToken)));

    /// <summary>"Vaxtı dəyiş": back to <c>Scheduled</c> at the new time.</summary>
    [HttpPut("{id:guid}/time")]
    public async Task<ActionResult<ScheduleView>> Reschedule(
        Guid id, [FromBody] RescheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new RescheduleCommand(UserId, id, request.ScheduledAt), cancellationToken));
    }

    /// <summary>
    /// "Çapa ver": into the kiosk's queue now. <c>409</c> with the reason when the kiosk is not
    /// online or cannot print it (paper, printer); the app then offers another time.
    /// </summary>
    [HttpPost("{id:guid}/print-now")]
    [Idempotent]
    public async Task<ActionResult<ScheduleView>> PrintNow(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new PrintScheduleNowCommand(UserId, id), cancellationToken));

    /// <summary>Cancels it; what was paid goes back to the wallet.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new CancelScheduleCommand(UserId, id), cancellationToken);
        return NoContent();
    }
}

/// <param name="Kiosk">The kiosk's id or code.</param>
public sealed record CreateScheduleRequest(
    string? Kiosk, Guid FileId, PrintOptions? Options, DateTime ScheduledAt, decimal ExpectedAmount);

public sealed record RescheduleRequest(DateTime ScheduledAt);

public sealed record ScheduleList(IReadOnlyList<ScheduleView> Items);
