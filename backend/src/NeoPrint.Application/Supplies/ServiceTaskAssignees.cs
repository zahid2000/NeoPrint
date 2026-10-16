using MediatR;

using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Supplies;

// ---------------------------------------------------------------------------
// GET admin/service-tasks/assignees?kioskId= - who a task of this kiosk can be
// given to ("Servis tapşırıqları" page, plan section 8A.3)
// ---------------------------------------------------------------------------

/// <param name="Operator">
/// Finishes tasks and does not hand them out: the accounts the system itself
/// picks from (<see cref="ServiceTaskDesk.PickOperatorAsync"/>). A manager
/// may still be chosen by hand.
/// </param>
/// <param name="ActiveTasks">What the account has on its hands now: tasks given to it and not finished.</param>
public sealed record ServiceTaskAssignee(Guid Id, string FullName, bool Operator, int ActiveTasks);

public sealed record ListServiceTaskAssigneesQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<ServiceTaskAssignee>>;

/// <summary>
/// The accounts a task of the kiosk may be given to - the same test the
/// assignment itself applies: active, the kiosk in its scope, allowed to
/// finish tasks. Operators first, then the least busy.
/// </summary>
public sealed class ServiceTaskAssigneeHandler : IRequestHandler<ListServiceTaskAssigneesQuery, IReadOnlyList<ServiceTaskAssignee>>
{
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IServiceTaskRepository _tasks;

    public ServiceTaskAssigneeHandler(IAdminUserRepository admins, IPermissionResolver permissions, IServiceTaskRepository tasks)
    {
        _admins = admins;
        _permissions = permissions;
        _tasks = tasks;
    }

    public async Task<IReadOnlyList<ServiceTaskAssignee>> Handle(ListServiceTaskAssigneesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var caller = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        if (!caller.CanSee(request.KioskId))
        {
            throw new NotFoundException("kiosk");
        }

        var load = (await _tasks.ListActiveAsync(cancellationToken).ConfigureAwait(false))
            .Where(task => task.AssignedTo is not null)
            .GroupBy(task => task.AssignedTo!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var found = new List<ServiceTaskAssignee>();

        foreach (var admin in (await _admins.ListAsync(cancellationToken).ConfigureAwait(false)).Where(admin => admin.IsActive))
        {
            var effective = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

            if (!effective.IsActive || !effective.CanSee(request.KioskId) || !effective.Has(Permissions.ServiceTasksComplete))
            {
                continue;
            }

            found.Add(new ServiceTaskAssignee(
                admin.Id,
                admin.FullName,
                Operator: !effective.Has(Permissions.ServiceTasksManage),
                load.GetValueOrDefault(admin.Id)));
        }

        return
        [
            .. found
                .OrderByDescending(candidate => candidate.Operator)
                .ThenBy(candidate => candidate.ActiveTasks)
                .ThenBy(candidate => candidate.FullName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
