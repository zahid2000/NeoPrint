using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

/// <summary>
/// Opens the tasks the system asks for, gives a task to an operator, and tells
/// whoever has to know (plan section 2.15, "Servis tapşırıqları").
/// </summary>
/// <remarks>
/// One place for it, because three things open tasks - a rule that was
/// crossed, a forecast, an administrator - and all three assign and announce
/// the same way.
/// </remarks>
public sealed class ServiceTaskDesk
{
    private readonly IServiceTaskRepository _tasks;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IAdminNotifier _panels;
    private readonly IEmailSender _email;
    private readonly ILogger<ServiceTaskDesk> _logger;

    public ServiceTaskDesk(
        IServiceTaskRepository tasks,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IAdminNotifier panels,
        IEmailSender email,
        ILogger<ServiceTaskDesk> logger)
    {
        _tasks = tasks;
        _admins = admins;
        _permissions = permissions;
        _panels = panels;
        _email = email;
        _logger = logger;
    }

    /// <summary>
    /// The task of this key that is still to be done, made more urgent if it
    /// has to be - or a new one, given to an operator.
    /// </summary>
    /// <returns>The task, and whether it was opened now.</returns>
    public async Task<(ServiceTask Task, bool Opened)> EnsureAsync(
        Kiosk kiosk, ServiceTask wanted, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        ArgumentNullException.ThrowIfNull(wanted);

        if (wanted.Key is { } key
            && await _tasks.FindActiveByKeyAsync(key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (existing.RaisePriority(wanted.Priority, now))
            {
                // More urgent because the level fell further: the note says the level it is at now.
                existing.Notes = wanted.Notes ?? existing.Notes;
                await _tasks.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                await AnnounceAsync(existing, kiosk, assigned: false, cancellationToken).ConfigureAwait(false);
            }

            return (existing, false);
        }

        if (await PickOperatorAsync(kiosk.Id, cancellationToken).ConfigureAwait(false) is { } operatorId)
        {
            wanted.Assign(operatorId, ActorContext.System("serviceTasks.reason.autoAssigned"), now);
        }

        if (!await _tasks.TryAddAsync(wanted, cancellationToken).ConfigureAwait(false))
        {
            // Somebody opened the same task between the look and the write.
            var raced = await _tasks.FindActiveByKeyAsync(wanted.Key!, cancellationToken).ConfigureAwait(false);

            return (raced ?? wanted, false);
        }

        _logger.LogInformation(
            "Service task {TaskId} ({Type}, {Priority}) opened for kiosk {KioskId}; assigned to {AssignedTo}.",
            wanted.Id, wanted.Type, wanted.Priority, kiosk.Id, wanted.AssignedTo);

        await AnnounceAsync(wanted, kiosk, assigned: wanted.AssignedTo is not null, cancellationToken).ConfigureAwait(false);

        if (wanted.AssignedTo is null)
        {
            await TellManagersAsync(wanted, kiosk, cancellationToken).ConfigureAwait(false);
        }

        return (wanted, true);
    }

    /// <summary>
    /// A task the system opened and could give to nobody - the kiosk has no
    /// operator - waits in the pool, and those who hand tasks out are told by
    /// e-mail: without that it would wait until somebody happened to look
    /// (<c>docs/DECISIONS.md</c>, T-260).
    /// </summary>
    /// <remarks>
    /// "Those who hand tasks out" are the active accounts with
    /// <c>service.tasks.manage</c> that see the kiosk - the SuperAdmin always
    /// among them. A task an administrator opens by hand is not announced
    /// this way: they are looking at it.
    /// </remarks>
    private async Task TellManagersAsync(ServiceTask task, Kiosk kiosk, CancellationToken cancellationToken)
    {
        var place = kiosk.Code + " — " + kiosk.Name;

        foreach (var admin in (await _admins.ListAsync(cancellationToken).ConfigureAwait(false))
                     .Where(admin => admin.IsActive && !string.IsNullOrWhiteSpace(admin.Email)))
        {
            var effective = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

            if (!effective.IsActive || !effective.Has(Permissions.ServiceTasksManage) || !effective.CanSee(kiosk.Id))
            {
                continue;
            }

            try
            {
                await _email
                    .SendAsync(
                        admin.Email,
                        Messages.Write(UserMessage.Of("email.serviceTaskUnassigned.subject", ("what", task.Type.ToString()), ("kiosk", kiosk.Code))),
                        Messages.Write(UserMessage.Of(
                            "email.serviceTaskUnassigned.body",
                            ("what", task.Type.ToString()),
                            ("kiosk", System.Net.WebUtility.HtmlEncode(place)),
                            ("priority", task.Priority.ToString()),
                            ("notes", System.Net.WebUtility.HtmlEncode(Messages.WriteStored(task.Notes) ?? string.Empty)))),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The task is in the pool either way; a mail server that is down does not undo it.
                _logger.LogWarning(exception, "Service task {TaskId}: the notice that nobody could be given it was not sent.", task.Id);
            }
        }

        _logger.LogWarning(
            "Service task {TaskId} ({Type}) for kiosk {KioskId} could be given to nobody: the kiosk has no operator.",
            task.Id, task.Type, kiosk.Id);
    }

    /// <summary>
    /// The operator who looks after the kiosk and has the least to do (plan
    /// section 2.15); null when there is none - the task then waits in the
    /// pool.
    /// </summary>
    /// <remarks>
    /// An operator is an account that finishes tasks and does not hand them
    /// out: one with <c>service.tasks.complete</c> and without
    /// <c>service.tasks.manage</c>. Without that, every task would land on
    /// the SuperAdmin, who holds every permission and sees every kiosk
    /// (<c>docs/DECISIONS.md</c>).
    /// </remarks>
    public async Task<Guid?> PickOperatorAsync(Guid kioskId, CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();

        foreach (var admin in (await _admins.ListAsync(cancellationToken).ConfigureAwait(false)).Where(admin => admin.IsActive))
        {
            if (await CanDoAsync(admin.Id, kioskId, cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(admin.Id);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var load = (await _tasks.ListActiveAsync(cancellationToken).ConfigureAwait(false))
            .Where(task => task.AssignedTo is not null)
            .GroupBy(task => task.AssignedTo!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        // The fewest tasks; between equals the same one every time.
        return candidates.OrderBy(id => load.GetValueOrDefault(id)).ThenBy(id => id).First();
    }

    /// <summary>Whether an account is an operator of this kiosk: active, it sees the kiosk, finishes tasks and does not manage them.</summary>
    public async Task<bool> CanDoAsync(Guid adminId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminId, cancellationToken).ConfigureAwait(false);

        return effective.IsActive
               && effective.CanSee(kioskId)
               && effective.Has(Permissions.ServiceTasksComplete)
               && !effective.Has(Permissions.ServiceTasksManage);
    }

    /// <summary>
    /// Tells the panels, and - when the task has just been given to somebody -
    /// that operator, by e-mail as well.
    /// </summary>
    public async Task AnnounceAsync(ServiceTask task, Kiosk? kiosk, bool assigned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);

        var holder = task.AssignedTo is { } adminId
            ? await _admins.FindByIdAsync(adminId, cancellationToken).ConfigureAwait(false)
            : null;
        var view = ServiceTaskView.Of(task, kiosk, holder?.FullName);

        if (!assigned || holder is null)
        {
            await _panels.ServiceTaskUpdatedAsync(view, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _panels.ServiceTaskAssignedAsync(view, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(holder.Email))
        {
            return;
        }

        try
        {
            var where = kiosk?.Code ?? task.KioskId.ToString();
            var place = kiosk is null ? where : kiosk.Code + " — " + kiosk.Name;

            // The type and the priority go in as their names; the catalog writes them in the reader's words.
            await _email
                .SendAsync(
                    holder.Email,
                    Messages.Write(UserMessage.Of("email.serviceTask.subject", ("what", task.Type.ToString()), ("kiosk", where))),
                    Messages.Write(UserMessage.Of(
                        "email.serviceTask.body",
                        ("what", task.Type.ToString()),
                        ("kiosk", System.Net.WebUtility.HtmlEncode(place)),
                        ("priority", task.Priority.ToString()),
                        ("notes", System.Net.WebUtility.HtmlEncode(Messages.WriteStored(task.Notes) ?? string.Empty)))),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The task is in the panel either way; a mail server that is down does not undo an assignment.
            _logger.LogWarning(exception, "Service task {TaskId}: the e-mail to its operator was not sent.", task.Id);
        }
    }
}
