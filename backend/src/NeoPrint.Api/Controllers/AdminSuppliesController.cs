using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Supplies;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Xəbərdarlıq qaydaları" and the alerts they raise (plan section 6, "Admin
/// Supplies / Service"; section 2.15).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminSuppliesController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminSuppliesController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    [HttpGet("supply-rules")]
    [HasPermission(Permissions.SuppliesView)]
    public async Task<ActionResult<IReadOnlyList<SupplyRuleView>>> Rules(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListSupplyRulesQuery(), cancellationToken));

    /// <summary>A rule for every kiosk, or for the kiosks it names - where it replaces the global one.</summary>
    [HttpPost("supply-rules")]
    [HasPermission(Permissions.SupplyRulesManage)]
    [Idempotent]
    public async Task<ActionResult<SupplyRuleView>> CreateRule([FromBody] SupplyRuleInput request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CreateSupplyRuleCommand(AdminId, request, _currentUser.Ip), cancellationToken));

    [HttpPut("supply-rules/{id:guid}")]
    [HasPermission(Permissions.SupplyRulesManage)]
    public async Task<ActionResult<SupplyRuleView>> UpdateRule(Guid id, [FromBody] SupplyRuleInput request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new UpdateSupplyRuleCommand(AdminId, id, request, _currentUser.Ip), cancellationToken));

    [HttpDelete("supply-rules/{id:guid}")]
    [HasPermission(Permissions.SupplyRulesManage)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DeleteSupplyRuleCommand(AdminId, id, _currentUser.Ip), cancellationToken);

        return NoContent();
    }

    /// <summary>What is below its rule now; <c>active=false</c> for what was, in the kiosks the account sees.</summary>
    [HttpGet("supply-alerts")]
    [HasPermission(Permissions.SuppliesView)]
    public async Task<ActionResult<SupplyAlertPage>> Alerts(
        [FromQuery] bool? active = true,
        [FromQuery] Guid? kioskId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListSupplyAlertsQuery(AdminId, active, kioskId, page, pageSize), cancellationToken));
}

/// <summary>
/// "Servis tapşırıqları" (plan section 6; sections 2.15, 8B): the tasks of the
/// kiosks an account sees. An operator starts and finishes; an administrator
/// opens, assigns and cancels.
/// </summary>
[ApiController]
[Route("api/admin/service-tasks")]
public sealed class AdminServiceTasksController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminServiceTasksController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>What is still to be done first, the most urgent on top. <c>mine=true</c>: only the caller's.</summary>
    [HttpGet]
    [HasPermission(Permissions.ServiceTasksView)]
    public async Task<ActionResult<ServiceTaskPage>> List(
        [FromQuery] string[]? status,
        [FromQuery] Guid? kioskId = null,
        [FromQuery] bool mine = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListServiceTasksQuery(AdminId, status, kioskId, mine, page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.ServiceTasksView)]
    public async Task<ActionResult<ServiceTaskView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetServiceTaskQuery(AdminId, id), cancellationToken));

    [HttpPost]
    [HasPermission(Permissions.ServiceTasksManage)]
    [Idempotent]
    public async Task<ActionResult<ServiceTaskView>> Create([FromBody] CreateServiceTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new CreateServiceTaskCommand(
                AdminId, request.KioskId, request.Type, request.Priority, request.Notes, request.DueAt, request.AssignTo, _currentUser.Ip),
            cancellationToken));
    }

    /// <summary>Its priority, its due time and its note, while it is still to be done.</summary>
    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.ServiceTasksManage)]
    public async Task<ActionResult<ServiceTaskView>> Update(Guid id, [FromBody] UpdateServiceTaskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new UpdateServiceTaskCommand(AdminId, id, request.Priority, request.Notes, request.DueAt, _currentUser.Ip), cancellationToken));
    }

    /// <summary>Gives the task to an operator of its kiosk; without one, back to the pool.</summary>
    [HttpPost("{id:guid}/assign")]
    [HasPermission(Permissions.ServiceTasksManage)]
    public async Task<ActionResult<ServiceTaskView>> Assign(Guid id, [FromBody] AssignServiceTaskRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new AssignServiceTaskCommand(AdminId, id, request?.AssignTo, _currentUser.Ip), cancellationToken));

    /// <summary>"Qəbul et": a task in the pool becomes the caller's, not started yet.</summary>
    [HttpPost("{id:guid}/accept")]
    [HasPermission(Permissions.ServiceTasksComplete)]
    public async Task<ActionResult<ServiceTaskView>> Accept(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new AcceptServiceTaskCommand(AdminId, id, _currentUser.Ip), cancellationToken));

    /// <summary>"Başla". A task in the pool becomes the caller's.</summary>
    [HttpPost("{id:guid}/start")]
    [HasPermission(Permissions.ServiceTasksComplete)]
    public async Task<ActionResult<ServiceTaskView>> Start(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new StartServiceTaskCommand(AdminId, id), cancellationToken));

    /// <summary>"Tamamla": the tray is filled or the cartridge changed, and what came from the warehouse leaves its count.</summary>
    [HttpPost("{id:guid}/complete")]
    [HasPermission(Permissions.ServiceTasksComplete)]
    [Idempotent]
    public async Task<ActionResult<ServiceTaskView>> Complete(Guid id, [FromBody] CompleteServiceTaskRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new CompleteServiceTaskCommand(
                AdminId,
                id,
                request?.Paper,
                request?.RefillMode,
                request?.Sheets,
                request?.InventoryItemId,
                request?.Quantity,
                request?.Note,
                _currentUser.Ip),
            cancellationToken));

    /// <summary>Not to be done after all. The reason is mandatory and goes to the audit log.</summary>
    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.ServiceTasksManage)]
    public async Task<ActionResult<ServiceTaskView>> Cancel(Guid id, [FromBody] CancelServiceTaskRequest? request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CancelServiceTaskCommand(AdminId, id, request?.Reason, _currentUser.Ip), cancellationToken));
}

public sealed record CreateServiceTaskRequest(Guid KioskId, string Type, string? Priority, string? Notes, DateTime? DueAt, Guid? AssignTo);

public sealed record UpdateServiceTaskRequest(string Priority, string? Notes, DateTime? DueAt);

public sealed record AssignServiceTaskRequest(Guid? AssignTo);

public sealed record CompleteServiceTaskRequest(
    string? Paper, PaperRefillMode? RefillMode, int? Sheets, Guid? InventoryItemId, decimal? Quantity, string? Note);

public sealed record CancelServiceTaskRequest(string? Reason);

/// <summary>"Anbar" (plan section 6; section 2.15): what is kept for the kiosks, and how its count moved.</summary>
[ApiController]
[Route("api/admin/inventory")]
public sealed class AdminInventoryController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminInventoryController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    private Guid AdminId => _currentUser.AdminUserId ?? throw new UnauthorizedAccessException();

    /// <summary>What is running out first. <c>printerModel</c>: only what fits that printer.</summary>
    [HttpGet("items")]
    [HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<IReadOnlyList<InventoryItemView>>> Items(
        [FromQuery] bool includeInactive = false,
        [FromQuery] string? printerModel = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListInventoryItemsQuery(includeInactive, printerModel, type), cancellationToken));

    [HttpPost("items")]
    [HasPermission(Permissions.InventoryManage)]
    [Idempotent]
    public async Task<ActionResult<InventoryItemView>> CreateItem([FromBody] InventoryItemInput request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new CreateInventoryItemCommand(AdminId, request, _currentUser.Ip), cancellationToken));

    [HttpPut("items/{id:guid}")]
    [HasPermission(Permissions.InventoryManage)]
    public async Task<ActionResult<InventoryItemView>> UpdateItem(Guid id, [FromBody] InventoryItemInput request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new UpdateInventoryItemCommand(AdminId, id, request, _currentUser.Ip), cancellationToken));

    [HttpGet("movements")]
    [HasPermission(Permissions.InventoryView)]
    public async Task<ActionResult<InventoryMovementPage>> Movements(
        [FromQuery] Guid? itemId = null,
        [FromQuery] Guid? kioskId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
        => Ok(await _sender.Send(new ListInventoryMovementsQuery(itemId, kioskId, page, pageSize), cancellationToken));

    /// <summary>
    /// "Özümə götürürəm" (plan section 8B, "Anbar"): an operator writes down
    /// what they took from the shelf. Always in the caller's own name, and
    /// never more than the shelf holds - which is why it takes the operator
    /// panel's permission and not the warehouse keeper's.
    /// </summary>
    [HttpPost("items/{id:guid}/take")]
    [HasPermission(Permissions.OperatorPanel)]
    [Idempotent]
    public async Task<ActionResult<InventoryItemView>> Take(Guid id, [FromBody] TakeInventoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new RecordInventoryMovementCommand(
                AdminId, id, nameof(Domain.Supplies.InventoryMovementType.IssueToOperator), request.Quantity, null, request.Note, _currentUser.Ip),
            cancellationToken));
    }

    /// <summary>A purchase, a hand-out, a write-off or a recount. Never below zero.</summary>
    [HttpPost("movements")]
    [HasPermission(Permissions.InventoryManage)]
    [Idempotent]
    public async Task<ActionResult<InventoryItemView>> Move([FromBody] InventoryMovementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(
            new RecordInventoryMovementCommand(AdminId, request.ItemId, request.Type, request.Quantity, request.KioskId, request.Note, _currentUser.Ip),
            cancellationToken));
    }
}

public sealed record InventoryMovementRequest(Guid ItemId, string Type, decimal Quantity, Guid? KioskId, string? Note);

public sealed record TakeInventoryRequest(decimal Quantity, string? Note);
