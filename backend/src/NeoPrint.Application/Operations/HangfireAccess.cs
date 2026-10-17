using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;

namespace NeoPrint.Application.Operations;

/// <summary>
/// Whether an administrator may use the Hangfire panel (T-167, T-215): an
/// active account holding the SuperAdmin role - asked again on every request
/// to the panel, so a role taken away or an account switched off locks the
/// panel at once.
/// </summary>
public sealed record CanUseHangfireQuery(Guid AdminUserId) : IRequest<bool>;

/// <summary>The panel was opened: audit <c>hangfire.opened</c>.</summary>
/// <returns>False when the account may not open it (nothing is written then).</returns>
public sealed record OpenHangfireCommand(Guid AdminUserId, string? Ip) : IRequest<bool>;

/// <summary>Something was done in the panel - a job started, requeued, deleted: audit <c>hangfire.action</c>.</summary>
/// <param name="Action">The panel's own path for it, <c>/recurring/trigger</c>.</param>
/// <param name="Jobs">The jobs it was done to, as the panel names them.</param>
public sealed record RecordHangfireActionCommand(Guid AdminUserId, string Action, IReadOnlyList<string> Jobs, string? Ip) : IRequest<Unit>;

public sealed class HangfireAccessHandlers :
    IRequestHandler<CanUseHangfireQuery, bool>,
    IRequestHandler<OpenHangfireCommand, bool>,
    IRequestHandler<RecordHangfireActionCommand, Unit>
{
    private readonly IAdminUserRepository _admins;
    private readonly IAdminRoleRepository _roles;
    private readonly IAuditWriter _audit;

    public HangfireAccessHandlers(IAdminUserRepository admins, IAdminRoleRepository roles, IAuditWriter audit)
    {
        _admins = admins;
        _roles = roles;
        _audit = audit;
    }

    public async Task<bool> Handle(CanUseHangfireQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var admin = await _admins.FindByIdAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        if (admin is not { IsActive: true } || admin.RoleIds.Count == 0)
        {
            return false;
        }

        var roles = await _roles.FindByIdsAsync(admin.RoleIds, cancellationToken).ConfigureAwait(false);

        return roles.Any(role => role.IsSuperAdmin);
    }

    public async Task<bool> Handle(OpenHangfireCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Handle(new CanUseHangfireQuery(request.AdminUserId), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _audit
            .WriteAsync(new AuditEntry(request.AdminUserId, "hangfire.opened", "Hangfire", null, null, null, request.Ip, null), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    public async Task<Unit> Handle(RecordHangfireActionCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "hangfire.action",
                    "Hangfire",
                    request.Jobs.Count == 0 ? null : string.Join(",", request.Jobs.Take(20)),
                    null,
                    request.Action,
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
