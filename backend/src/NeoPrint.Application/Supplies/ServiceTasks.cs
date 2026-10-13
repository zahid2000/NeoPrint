using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Kiosks;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

// ---------------------------------------------------------------------------
// "Servis tapşırıqları" (plan sections 2.15, 8A.3, 8B)
// ---------------------------------------------------------------------------

/// <param name="Statuses">Names of <see cref="ServiceTaskStatus"/>; empty for every status.</param>
/// <param name="Mine">Only the tasks that are with the caller.</param>
public sealed record ListServiceTasksQuery(
    Guid AdminUserId, IReadOnlyCollection<string>? Statuses, Guid? KioskId, bool Mine, int Page, int PageSize) : IRequest<ServiceTaskPage>;

public sealed record ServiceTaskPage(IReadOnlyList<ServiceTaskView> Items, long Total, int Page, int PageSize);

public sealed record GetServiceTaskQuery(Guid AdminUserId, Guid TaskId) : IRequest<ServiceTaskView>;

/// <summary>A task an administrator opens by hand.</summary>
/// <param name="AssignTo">An operator of the kiosk; null lets the system choose one.</param>
public sealed record CreateServiceTaskCommand(
    Guid AdminUserId, Guid KioskId, string Type, string? Priority, string? Notes, DateTime? DueAt, Guid? AssignTo, string? Ip)
    : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <summary>How urgent a task is, by when it is due, and its note - rewritten by an administrator while it is still to be done.</summary>
public sealed record UpdateServiceTaskCommand(Guid AdminUserId, Guid TaskId, string Priority, string? Notes, DateTime? DueAt, string? Ip)
    : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <param name="AssignTo">Null puts the task back in the pool.</param>
public sealed record AssignServiceTaskCommand(Guid AdminUserId, Guid TaskId, Guid? AssignTo, string? Ip)
    : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <summary>"Başla": the caller is at the kiosk. A task in the pool becomes theirs.</summary>
public sealed record StartServiceTaskCommand(Guid AdminUserId, Guid TaskId) : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <summary>
/// "Qəbul et" (plan section 8B, "Tapşırıqlarım"): an operator takes a task out
/// of the pool for themselves, without starting it yet. Taking one's own task
/// again changes nothing; one that is somebody else's is refused.
/// </summary>
public sealed record AcceptServiceTaskCommand(Guid AdminUserId, Guid TaskId, string? Ip) : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <summary>
/// "Tamamla": what was done. A paper refill moves the kiosk's counter; what
/// was taken from the warehouse leaves its count (plan section 9.9, step 5).
/// </summary>
/// <param name="Paper">"A4" or "A3"; for a refill it follows the task when left out.</param>
/// <param name="RefillMode">How the tray was filled; a refill is "Full" when left out.</param>
/// <param name="Sheets">The sheets added, or counted - not for "Full".</param>
/// <param name="Quantity">How many units of the warehouse item were used.</param>
public sealed record CompleteServiceTaskCommand(
    Guid AdminUserId,
    Guid TaskId,
    string? Paper,
    PaperRefillMode? RefillMode,
    int? Sheets,
    Guid? InventoryItemId,
    decimal? Quantity,
    string? Note,
    string? Ip)
    : IRequest<ServiceTaskView>, ITransactionalRequest;

public sealed record CancelServiceTaskCommand(Guid AdminUserId, Guid TaskId, string? Reason, string? Ip)
    : IRequest<ServiceTaskView>, ITransactionalRequest;

/// <summary>
/// <c>ServiceTaskEscalationJob</c> (plan section 6, every five minutes): a
/// Critical task nobody has started within
/// <c>Service.TaskOverdueEscalationMinutes</c> is put before the administrators.
/// </summary>
/// <returns>How many tasks were escalated now.</returns>
public sealed record EscalateOverdueServiceTasksCommand : IRequest<int>;

public sealed class ServiceTaskHandlers :
    IRequestHandler<AcceptServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<ListServiceTasksQuery, ServiceTaskPage>,
    IRequestHandler<GetServiceTaskQuery, ServiceTaskView>,
    IRequestHandler<CreateServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<UpdateServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<AssignServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<StartServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<CompleteServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<CancelServiceTaskCommand, ServiceTaskView>,
    IRequestHandler<EscalateOverdueServiceTasksCommand, int>
{
    public const int MaxNoteLength = 500;

    private readonly IServiceTaskRepository _tasks;
    private readonly IKioskRepository _kiosks;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly ISupplyEventRepository _supplies;
    private readonly IInventoryRepository _inventory;
    private readonly ServiceTaskDesk _desk;
    private readonly IncidentReporter _incidents;
    private readonly IKioskNotifier _kiosk;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<ServiceTaskHandlers> _logger;

    public ServiceTaskHandlers(
        IServiceTaskRepository tasks,
        IKioskRepository kiosks,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        ISupplyEventRepository supplies,
        IInventoryRepository inventory,
        ServiceTaskDesk desk,
        IncidentReporter incidents,
        IKioskNotifier kiosk,
        ISettingsReader settings,
        IAuditWriter audit,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<ServiceTaskHandlers> logger)
    {
        _tasks = tasks;
        _kiosks = kiosks;
        _admins = admins;
        _permissions = permissions;
        _supplies = supplies;
        _inventory = inventory;
        _desk = desk;
        _incidents = incidents;
        _kiosk = kiosk;
        _settings = settings;
        _audit = audit;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    public static string OverdueKey(Guid taskId) => $"service-task-overdue:{taskId:N}";

    /// <inheritdoc cref="ListServiceTasksQuery" />
    public async Task<ServiceTaskPage> Handle(ListServiceTasksQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);

        if (request.KioskId is { } asked && !effective.CanSee(asked))
        {
            return new ServiceTaskPage([], 0, page, size);
        }

        var statuses = new List<ServiceTaskStatus>();

        foreach (var name in request.Statuses ?? [])
        {
            if (!Enum.TryParse<ServiceTaskStatus>(name, ignoreCase: false, out var status) || !Enum.IsDefined(status))
            {
                throw new DomainException("serviceTasks.statusUnknown");
            }

            statuses.Add(status);
        }

        var (items, total) = await _tasks
            .ListAsync(
                statuses.Count == 0 ? null : statuses,
                request.KioskId,
                request.Mine ? request.AdminUserId : null,
                effective.AllKiosks ? null : effective.KioskIds,
                (page - 1) * size,
                size,
                cancellationToken)
            .ConfigureAwait(false);

        var kiosks = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(kiosk => kiosk.Id);
        var names = (await _admins.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(admin => admin.Id, admin => admin.FullName);

        return new ServiceTaskPage(
            [
                .. items.Select(task => ServiceTaskView.Of(
                    task, kiosks.GetValueOrDefault(task.KioskId), task.AssignedTo is { } holder ? names.GetValueOrDefault(holder) : null)),
            ],
            total,
            page,
            size);
    }

    /// <inheritdoc cref="GetServiceTaskQuery" />
    public async Task<ServiceTaskView> Handle(GetServiceTaskQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CreateServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(CreateServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(request.KioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        if (!Enum.TryParse<ServiceTaskType>(request.Type, ignoreCase: false, out var type) || !Enum.IsDefined(type))
        {
            throw new DomainException("serviceTasks.typeUnknown");
        }

        var priority = TaskPriority.Normal;

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && (!Enum.TryParse(request.Priority, ignoreCase: false, out priority) || !Enum.IsDefined(priority)))
        {
            throw new DomainException("serviceTasks.priorityUnknown");
        }

        var notes = Note(request.Notes);
        var now = _clock.UtcNow;

        if (request.DueAt is { } due && due <= now)
        {
            throw new DomainException("serviceTasks.dueInPast");
        }

        var task = ServiceTask.Open(
            kiosk.Id, type, priority, ServiceTaskSource.Manual, now, notes: notes, dueAt: request.DueAt, createdBy: request.AdminUserId);
        var actor = Admin(request.AdminUserId);
        var assignee = request.AssignTo;

        if (assignee is { } chosen)
        {
            await EnsureOperatorAsync(chosen, kiosk.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assignee = await _desk.PickOperatorAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }

        if (assignee is { } operatorId)
        {
            task.Assign(operatorId, actor, now);
        }

        await _tasks.TryAddAsync(task, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.create", task, before: null, request.Ip, cancellationToken).ConfigureAwait(false);
        await _desk.AnnounceAsync(task, kiosk, assigned: task.AssignedTo is not null, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="UpdateServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(UpdateServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);

        if (!Enum.TryParse<TaskPriority>(request.Priority, ignoreCase: false, out var priority) || !Enum.IsDefined(priority))
        {
            throw new DomainException("serviceTasks.priorityUnknown");
        }

        var now = _clock.UtcNow;

        if (request.DueAt is { } due && due <= now)
        {
            throw new DomainException("serviceTasks.dueInPast");
        }

        var before = $"{task.Priority} due={task.DueAt:O}";

        task.Edit(priority, Note(request.Notes), request.DueAt, now);

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.update", task, before, request.Ip, cancellationToken).ConfigureAwait(false);
        await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AssignServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(AssignServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);
        var before = task.AssignedTo;

        if (request.AssignTo is { } chosen)
        {
            await EnsureOperatorAsync(chosen, task.KioskId, cancellationToken).ConfigureAwait(false);
        }

        task.Assign(request.AssignTo, Admin(request.AdminUserId), _clock.UtcNow);

        if (task.AssignedTo == before)
        {
            return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
        }

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.assign", task, before?.ToString(), request.Ip, cancellationToken).ConfigureAwait(false);
        await _desk.AnnounceAsync(task, kiosk, assigned: task.AssignedTo is not null, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="AcceptServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(AcceptServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);

        if (task.AssignedTo == request.AdminUserId)
        {
            return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
        }

        if (task.AssignedTo is not null)
        {
            throw new DomainException("serviceTasks.notYours");
        }

        task.Assign(request.AdminUserId, Admin(request.AdminUserId), _clock.UtcNow);

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.accept", task, before: null, request.Ip, cancellationToken).ConfigureAwait(false);

        // To the panels only: nobody needs an e-mail about what they have just done themselves.
        await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="StartServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(StartServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);

        task.Start(request.AdminUserId, Admin(request.AdminUserId), _clock.UtcNow);

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);

        // Somebody is at it: whatever was said about its waiting is over.
        await _incidents
            .ResolveAsync(OverdueKey(task.Id), new UserMessage("incidents.resolution.serviceTaskStarted"), cancellationToken)
            .ConfigureAwait(false);
        await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CompleteServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(CompleteServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);

        // Only whoever started it says how it ended.
        if (task.Status == ServiceTaskStatus.InProgress && task.AssignedTo != request.AdminUserId)
        {
            throw new DomainException("serviceTasks.notYours");
        }

        if (task.Status != ServiceTaskStatus.InProgress)
        {
            throw new DomainException("serviceTasks.notInProgress");
        }

        if (kiosk is null)
        {
            throw new NotFoundException("kiosk");
        }

        var now = _clock.UtcNow;
        var completion = new ServiceTaskCompletion { Note = Note(request.Note) };

        if (task.Type == ServiceTaskType.RefillPaper)
        {
            // The tray the task is about, unless the operator says they filled the other one.
            var paper = !string.IsNullOrWhiteSpace(request.Paper) ? request.Paper.Trim().ToUpperInvariant()
                : task.Resource == SupplyResource.PaperA3 ? "A3"
                : "A4";
            var mode = request.RefillMode ?? PaperRefillMode.Full;
            var movement = kiosk.RefillPaper(paper, mode, request.Sheets ?? 0, request.AdminUserId, now);

            await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);
            await _supplies.AddAsync(movement, cancellationToken).ConfigureAwait(false);

            completion.Paper = paper;
            completion.SheetsAdded = movement.Delta;
        }
        else if (task.Resource is { } resource && task.Type is ServiceTaskType.ReplaceToner or ServiceTaskType.ReplaceDrum)
        {
            // "Toner dəyişdirildi": written down next to the paper movements. The
            // level itself is the printer's to report; its alert clears when it does.
            await _supplies
                .AddAsync(
                    new SupplyEvent
                    {
                        KioskId = kiosk.Id,
                        Resource = task.Variant is null ? resource.ToString() : $"{resource}:{task.Variant}",
                        Type = SupplyEventType.Replaced,
                        By = request.AdminUserId,
                        At = now,
                        DedupKey = $"task:{task.Id:N}",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.InventoryItemId is { } itemId)
        {
            var item = await _inventory.FindByIdAsync(itemId, cancellationToken).ConfigureAwait(false)
                       ?? throw new NotFoundException("inventoryItem");
            var used = item.Move(
                InventoryMovementType.UsedAtKiosk, request.Quantity ?? 1, request.AdminUserId, now, kiosk.Id, task.Id, completion.Note);

            await _inventory.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
            await _inventory.AddMovementAsync(used, cancellationToken).ConfigureAwait(false);

            completion.InventoryItemId = item.Id;
            completion.Quantity = request.Quantity ?? 1;
        }
        else if (request.Quantity is not null)
        {
            throw new DomainException("serviceTasks.quantityWithoutItem");
        }

        task.Complete(completion, Admin(request.AdminUserId), now);

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _incidents
            .ResolveAsync(OverdueKey(task.Id), new UserMessage("incidents.resolution.serviceTaskDone"), cancellationToken)
            .ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.complete", task, before: null, request.Ip, cancellationToken).ConfigureAwait(false);

        if (completion.SheetsAdded is not null)
        {
            // The kiosk checks orders against the tray; it fetches the new count now.
            await _kiosk.KioskConfigChangedAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);
        }

        await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CancelServiceTaskCommand" />
    public async Task<ServiceTaskView> Handle(CancelServiceTaskCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (task, kiosk) = await VisibleAsync(request.AdminUserId, request.TaskId, cancellationToken).ConfigureAwait(false);
        var reason = Note(request.Reason) ?? throw new DomainException("serviceTasks.reasonRequired");

        if (task.Status == ServiceTaskStatus.Cancelled)
        {
            return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
        }

        task.Cancel(reason, Admin(request.AdminUserId).Because(reason), _clock.UtcNow);

        await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
        await _incidents
            .ResolveAsync(OverdueKey(task.Id), new UserMessage("incidents.resolution.serviceTaskCancelled"), cancellationToken)
            .ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "serviceTasks.cancel", task, before: null, request.Ip, cancellationToken, reason).ConfigureAwait(false);
        await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(task, kiosk, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="EscalateOverdueServiceTasksCommand" />
    public async Task<int> Handle(EscalateOverdueServiceTasksCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var after = TimeSpan.FromMinutes(Math.Max(
            1,
            await _settings.GetIntAsync(SettingKeys.ServiceTaskOverdueEscalationMinutes, 60, cancellationToken).ConfigureAwait(false)));
        var now = _clock.UtcNow;
        var escalated = 0;

        foreach (var task in (await _tasks.ListActiveAsync(cancellationToken).ConfigureAwait(false)).Where(task => task.NeedsEscalation(now, after)))
        {
            var kiosk = await _kiosks.FindByIdAsync(task.KioskId, cancellationToken).ConfigureAwait(false);

            task.MarkEscalated(now);

            try
            {
                await _tasks.UpdateAsync(task, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException)
            {
                // Started or reassigned this very moment: the next run looks again.
                continue;
            }

            await _incidents
                .RaiseAsync(
                    IncidentType.Other,
                    IncidentSeverity.High,
                    UserMessage.Of(
                        "incidents.serviceTask.overdue",
                        ("what", task.Type.ToString()),
                        ("kiosk", kiosk?.Code ?? task.KioskId.ToString()),
                        ("minutes", (int)(now - (task.CriticalSince ?? task.CreatedAt)).TotalMinutes)),
                    OverdueKey(task.Id),
                    cancellationToken,
                    task.KioskId,
                    nameof(ServiceTask),
                    task.Id)
                .ConfigureAwait(false);

            _logger.LogWarning("Service task {TaskId} at kiosk {KioskId} is Critical and nobody has started it.", task.Id, task.KioskId);

            await _desk.AnnounceAsync(task, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);
            escalated++;
        }

        return escalated;
    }

    // -----------------------------------------------------------------------

    private ActorContext Admin(Guid adminId) => ActorContext.Admin(adminId, correlationId: _correlation.CorrelationId);

    /// <exception cref="NotFoundException">There is no such task, or it is at a kiosk the account does not see.</exception>
    private async Task<(ServiceTask Task, Kiosk? Kiosk)> VisibleAsync(Guid adminId, Guid taskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminId, cancellationToken).ConfigureAwait(false);
        var task = await _tasks.FindByIdAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (task is null || !effective.CanSee(task.KioskId))
        {
            throw new NotFoundException("serviceTask");
        }

        return (task, await _kiosks.FindByIdAsync(task.KioskId, cancellationToken).ConfigureAwait(false));
    }

    /// <exception cref="DomainException">The account cannot be given this kiosk's tasks.</exception>
    private async Task EnsureOperatorAsync(Guid adminId, Guid kioskId, CancellationToken cancellationToken)
    {
        var admin = await _admins.FindByIdAsync(adminId, cancellationToken).ConfigureAwait(false);

        if (admin is null || !admin.IsActive)
        {
            throw new NotFoundException("adminUser");
        }

        var effective = await _permissions.ResolveAsync(adminId, cancellationToken).ConfigureAwait(false);

        // Chosen by hand, a manager may take a task too - only the system keeps to operators.
        if (!effective.IsActive || !effective.CanSee(kioskId) || !effective.Has(Permissions.ServiceTasksComplete))
        {
            throw new DomainException("serviceTasks.assigneeCannot");
        }
    }

    private async Task<ServiceTaskView> ViewAsync(ServiceTask task, Kiosk? kiosk, CancellationToken cancellationToken)
    {
        var holder = task.AssignedTo is { } adminId
            ? await _admins.FindByIdAsync(adminId, cancellationToken).ConfigureAwait(false)
            : null;

        return ServiceTaskView.Of(task, kiosk, holder?.FullName);
    }

    /// <summary>What somebody wrote, trimmed; null when they wrote nothing.</summary>
    /// <exception cref="DomainException">Longer than a note may be.</exception>
    private static string? Note(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var note = text.Trim();

        return note.Length > MaxNoteLength
            ? throw new DomainException(UserMessage.Of("serviceTasks.noteTooLong", ("max", MaxNoteLength)))
            : note;
    }

    private Task AuditAsync(
        Guid adminId, string action, ServiceTask task, string? before, string? ip, CancellationToken cancellationToken, string? reason = null)
        => _audit.WriteAsync(
            new AuditEntry(
                adminId,
                action,
                nameof(ServiceTask),
                task.Id.ToString(),
                before,
                $"{task.Type} {task.Status} {task.Priority} assignedTo={task.AssignedTo}",
                ip,
                reason),
            cancellationToken);
}
