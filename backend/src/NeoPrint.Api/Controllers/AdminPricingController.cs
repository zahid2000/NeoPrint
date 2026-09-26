using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Admin;
using NeoPrint.Pricing;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Pricing profiles (plan sections 2.3, 8A.3 - the "Qiymət profilləri" page).
/// </summary>
[ApiController]
[Route("api/admin/pricing-profiles")]
[HasPermission(Permissions.PricingManage)]
public sealed class AdminPricingController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPricingController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PricingProfileResponse>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListPricingProfilesQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PricingProfileDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPricingProfileQuery(id), cancellationToken));

    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<PricingProfileResponse>> Create(
        [FromBody] PricingProfileInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CreatePricingProfileCommand(input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PricingProfileResponse>> Update(
        Guid id, [FromBody] PricingProfileInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdatePricingProfileCommand(id, input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeletePricingProfileCommand(id, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/default")]
    public async Task<ActionResult<PricingProfileResponse>> MakeDefault(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new MakeDefaultPricingProfileCommand(id, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    /// <summary>Points several kiosks at this profile at once.</summary>
    [HttpPut("{id:guid}/kiosks")]
    public async Task<ActionResult<PricingProfileDetail>> AssignKiosks(
        Guid id, [FromBody] AssignKiosksRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new AssignKiosksToProfileCommand(id, request.KioskIds ?? [], _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken));

    /// <summary>The calculator: a saved profile by id, or unsaved values from the form.</summary>
    [HttpPost("calculate")]
    public async Task<ActionResult<PriceBreakdown>> Calculate(
        [FromBody] CalculateRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CalculatePriceQuery(request.ProfileId, request.Draft, request.Options), cancellationToken));
}

public sealed record AssignKiosksRequest(IReadOnlyList<Guid>? KioskIds);

public sealed record CalculateRequest(Guid? ProfileId, PricingProfileInput? Draft, PrintOptionsDto Options);
