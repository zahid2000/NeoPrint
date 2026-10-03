using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.PrintJobs;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Kiosk üçün hazırla" (plan section 9.4, step 1; <c>docs/DECISIONS.md</c>
/// T-144): unpaid preparations for any kiosk, and orders paid in advance for
/// one kiosk, collected there with the personal QR.
/// </summary>
[ApiController]
[Route("api/prepared-prints")]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class PreparedPrintsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public PreparedPrintsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>Both kinds, newest first.</summary>
    [HttpGet]
    public async Task<ActionResult<PreparedPrintList>> List(CancellationToken cancellationToken)
        => Ok(new PreparedPrintList(await _sender.Send(new ListMyPreparedPrintsQuery(UserId), cancellationToken)));

    /// <summary>A file and the options chosen so far, unpaid; the rest is chosen at the kiosk.</summary>
    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<MyPreparedPrintView>> Create(
        [FromBody] PreparePrintRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new CreatePreparedPrintCommand(UserId, request.FileId, request.Options ?? PreparedOptions.None),
            cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new RemovePreparedPrintCommand(UserId, id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Paid in advance at one kiosk: an order and its payment, paid through
    /// <c>payments/{paymentId}/pay-with-app</c>. <c>expectedAmount</c> is the price
    /// the app showed; <c>409 printJobs.priceChanged</c> when the kiosk's prices changed since.
    /// </summary>
    [HttpPost("paid")]
    [Idempotent]
    public async Task<ActionResult<SessionPrintJobResponse>> CreatePaid(
        [FromBody] PaidPreparationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new CreatePaidPreparationCommand(
                UserId, request.Kiosk ?? string.Empty, request.FileId, request.Options ?? PrintOptions.Default, request.ExpectedAmount),
            cancellationToken));
    }

    /// <summary>Cancels an order paid in advance and not collected; the money goes back to the wallet.</summary>
    [HttpPost("paid/{jobId:guid}/cancel")]
    public async Task<IActionResult> CancelPaid(Guid jobId, CancellationToken cancellationToken)
    {
        await _sender.Send(new CancelHeldPrintJobCommand(UserId, jobId), cancellationToken);
        return NoContent();
    }
}

public sealed record PreparePrintRequest(Guid FileId, PreparedOptions? Options);

/// <param name="Kiosk">The kiosk's id or code.</param>
public sealed record PaidPreparationRequest(string? Kiosk, Guid FileId, PrintOptions? Options, decimal ExpectedAmount);

public sealed record PreparedPrintList(IReadOnlyList<MyPreparedPrintView> Items);
