using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Kiosk records (plan sections 6 "Admin Kiosks", 8A.3 "Kiosklar", "Kiosk
/// yarat/redaktə").
/// </summary>
/// <remarks>
/// Each part of the form has its own endpoint and its own permission, as the
/// plan lists them: limits need <c>kiosks.limits.manage</c>, the pricing
/// profile <c>pricing.manage</c>, the fiscal terminal <c>fiscal.manage</c>. The
/// panel shows each part only to those who may change it. Printers, commands
/// and telemetry arrive with the kiosk itself in step 3.
/// </remarks>
[ApiController]
[Route("api/admin/kiosks")]
public sealed class AdminKiosksController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminKiosksController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>The kiosks the caller's scope covers (plan section 8A.2).</summary>
    [HttpGet]
    [HasPermission(Permissions.KiosksView)]
    public async Task<ActionResult<IReadOnlyList<KioskListItem>>> List(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListKiosksQuery(AdminId), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.KiosksView)]
    public async Task<ActionResult<KioskDetail>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskQuery(id, AdminId), cancellationToken));

    /// <summary>Every device that is or was bound to the kiosk (the "Cihaz" tab).</summary>
    [HttpGet("{id:guid}/devices")]
    [HasPermission(Permissions.DevicesView)]
    public async Task<ActionResult<IReadOnlyList<KioskDeviceHistoryItem>>> Devices(
        Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListKioskDevicesQuery(id, AdminId), cancellationToken));

    /// <summary>Creates a kiosk with no device; it starts NotActivated.</summary>
    [HttpPost]
    [Idempotent]
    [HasPermission(Permissions.KiosksCreate)]
    public async Task<ActionResult<KioskDetail>> Create(
        [FromBody] CreateKioskInput input, CancellationToken cancellationToken)
    {
        var created = await _sender.Send(new CreateKioskCommand(input, AdminId, _currentUser.Ip), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.KiosksEdit)]
    public async Task<ActionResult<KioskDetail>> Update(
        Guid id, [FromBody] UpdateKioskInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new UpdateKioskCommand(id, input, AdminId, _currentUser.Ip), cancellationToken));

    /// <summary>Only a kiosk that never had a device can be deleted.</summary>
    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.KiosksDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteKioskCommand(id, AdminId, _currentUser.Ip), cancellationToken);
        return NoContent();
    }

    /// <summary>{ "maxFileSizeMb": int?, "maxPages": int? } - null is "Default istifadə et".</summary>
    [HttpPut("{id:guid}/limits")]
    [HasPermission(Permissions.KioskLimitsManage)]
    public async Task<ActionResult<KioskDetail>> SetLimits(
        Guid id, [FromBody] KioskLimitsRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetKioskLimitsCommand(id, request.MaxFileSizeMb, request.MaxPages, AdminId, _currentUser.Ip),
            cancellationToken));

    /// <summary>{ "pricingProfileId": guid? } - null falls back to the default profile.</summary>
    [HttpPut("{id:guid}/pricing-profile")]
    [HasPermission(Permissions.PricingManage)]
    public async Task<ActionResult<KioskDetail>> SetPricingProfile(
        Guid id, [FromBody] KioskPricingProfileRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetKioskPricingProfileCommand(id, request.PricingProfileId, AdminId, _currentUser.Ip),
            cancellationToken));

    [HttpPut("{id:guid}/fiscal-terminal")]
    [HasPermission(Permissions.FiscalManage)]
    public async Task<ActionResult<KioskDetail>> SetFiscalTerminal(
        Guid id, [FromBody] KioskFiscalTerminalRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetKioskFiscalTerminalCommand(id, request.FiscalTerminalId, AdminId, _currentUser.Ip),
            cancellationToken));

    /// <summary>{ "a4": int, "a3": int } - tray capacities in sheets.</summary>
    [HttpPut("{id:guid}/paper/capacity")]
    [HasPermission(Permissions.KiosksEdit)]
    public async Task<ActionResult<KioskDetail>> SetPaperCapacity(
        Guid id, [FromBody] KioskPaperCapacityRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetKioskPaperCapacityCommand(id, request.A4, request.A3, AdminId, _currentUser.Ip),
            cancellationToken));

    /// <summary>{ "pin": "1234" } - replaces the kiosk's admin PIN; only its hash is kept.</summary>
    [HttpPost("{id:guid}/admin-pin")]
    [HasPermission(Permissions.KiosksEdit)]
    public async Task<ActionResult<KioskDetail>> SetAdminPin(
        Guid id, [FromBody] KioskAdminPinRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetKioskAdminPinCommand(id, request.Pin ?? string.Empty, AdminId, _currentUser.Ip),
            cancellationToken));

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    // -----------------------------------------------------------------------
    // Printers and commands (plan sections 7.7, 7.8)
    // -----------------------------------------------------------------------

    /// <summary>The printers tab: what the kiosk found, what is selected, what may be offered.</summary>
    [HttpGet("{id:guid}/printers")]
    [HasPermission(Permissions.PrintersView)]
    public async Task<ActionResult<KioskPrintersView>> Printers(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskPrintersQuery(AdminId, id), cancellationToken));

    /// <summary>"Default et": the printer the kiosk prints on.</summary>
    [HttpPut("{id:guid}/printer")]
    [HasPermission(Permissions.PrintersSelect)]
    public async Task<ActionResult<KioskPrintersView>> SelectPrinter(
        Guid id, [FromBody] SelectPrinterRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SelectKioskPrinterCommand(AdminId, id, request?.Name ?? string.Empty), cancellationToken));

    /// <summary>Switches capabilities off for the selected printer; null follows what was detected.</summary>
    [HttpPut("{id:guid}/capabilities")]
    [HasPermission(Permissions.PrinterCapabilitiesManage)]
    public async Task<ActionResult<KioskPrintersView>> Capabilities(
        Guid id, [FromBody] CapabilityOverridesDto request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetCapabilityOverridesCommand(AdminId, id, request?.Color, request?.Duplex, request?.A3), cancellationToken));

    /// <summary>Asks the kiosk to look for printers again.</summary>
    [HttpPost("{id:guid}/printers/refresh")]
    [HasPermission(Permissions.PrintersRefresh)]
    public async Task<ActionResult<KioskCommandView>> RefreshPrinters(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SendKioskCommandCommand(AdminId, id, Domain.Kiosks.KioskCommandType.RefreshPrinters), cancellationToken));

    /// <summary>Prints the kiosk's test page; the result comes back as CommandResult.</summary>
    [HttpPost("{id:guid}/test-print")]
    [HasPermission(Permissions.PrintersTestPrint)]
    public async Task<ActionResult<KioskCommandView>> TestPrint(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SendKioskCommandCommand(AdminId, id, Domain.Kiosks.KioskCommandType.TestPrint), cancellationToken));

    /// <summary>The supplies tab: paper counters and their latest movements (plan section 2.15).</summary>
    [HttpGet("{id:guid}/supplies")]
    [HasPermission(Permissions.SuppliesView)]
    public async Task<ActionResult<KioskSuppliesView>> Supplies(Guid id, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new GetKioskSuppliesQuery(AdminId, id, limit), cancellationToken));

    /// <summary>"Kağız doldur": add sheets, fill the tray, or set the counted number.</summary>
    [HttpPost("{id:guid}/paper/refill")]
    [HasPermission(Permissions.PaperRefill)]
    [Idempotent]
    public async Task<ActionResult<KioskSuppliesView>> RefillPaper(
        Guid id, [FromBody] PaperRefillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new RefillKioskPaperCommand(AdminId, id, request.Paper ?? string.Empty, request.Mode, request.Sheets), cancellationToken));
    }

    [HttpGet("{id:guid}/commands")]
    [HasPermission(Permissions.KiosksView)]
    public async Task<ActionResult<IReadOnlyList<KioskCommandView>>> Commands(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListKioskCommandsQuery(AdminId, id), cancellationToken));
}

public sealed record SelectPrinterRequest(string? Name);


public sealed record KioskLimitsRequest(int? MaxFileSizeMb, int? MaxPages);

public sealed record KioskPricingProfileRequest(Guid? PricingProfileId);

public sealed record KioskFiscalTerminalRequest(string? FiscalTerminalId);

public sealed record KioskPaperCapacityRequest(int A4, int A3);

public sealed record KioskAdminPinRequest(string? Pin);

/// <param name="Mode">Add (sheets put in), Full (to capacity) or Exact (counted).</param>
public sealed record PaperRefillRequest(string? Paper, Domain.Kiosks.PaperRefillMode Mode, int Sheets);
