using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Pricing campaigns (T-436): the "Kampaniyalar" tab of the pricing page -
/// for a while or until switched off, at every kiosk or at chosen ones, a
/// percentage off or prices of their own.
/// </summary>
[ApiController]
[Route("api/admin/pricing-campaigns")]
[HasPermission(Permissions.PricingManage)]
public sealed class AdminPricingCampaignsController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminPricingCampaignsController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PricingCampaignResponse>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListPricingCampaignsQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PricingCampaignResponse>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetPricingCampaignQuery(id), cancellationToken));

    [HttpPost]
    [Idempotent]
    public async Task<ActionResult<PricingCampaignResponse>> Create(
        [FromBody] PricingCampaignInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CreatePricingCampaignCommand(input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PricingCampaignResponse>> Update(
        Guid id, [FromBody] PricingCampaignInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdatePricingCampaignCommand(id, input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeletePricingCampaignCommand(id, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken);
        return NoContent();
    }
}
