using MediatR;

using NeoPrint.Application.Localization;
using NeoPrint.Domain.Audit;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Admin;

// ---------------------------------------------------------------------------
// Audit log (plan section 8A.3, "Audit log"): GET admin/audit-logs
// ---------------------------------------------------------------------------

/// <param name="Action">The action's name, or the start of it: <c>kiosks.</c> is every kiosk action.</param>
/// <param name="EntityType">What was acted on: <c>Kiosk</c>, <c>PrintJob</c>, …</param>
/// <param name="EntityId">Which one.</param>
/// <param name="AdminUserId">Who did it.</param>
/// <param name="ActorType">An administrator, a kiosk, the system.</param>
/// <param name="CorrelationId">Everything one request did.</param>
public sealed record AuditLogFilter(
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    Guid? AdminUserId = null,
    string? ActorType = null,
    DateTime? From = null,
    DateTime? To = null,
    string? CorrelationId = null);

/// <summary>The <c>audit_logs</c> collection, read (it is written through <see cref="Abstractions.IAuditWriter"/>).</summary>
public interface IAuditLogReader
{
    /// <param name="kioskIds">
    /// Null for every entry. Otherwise only the entries about these kiosks:
    /// an account with a kiosk scope reads what was done to its own kiosks.
    /// </param>
    Task<(IReadOnlyList<AuditLog> Items, long Total)> ListAsync(
        AuditLogFilter filter,
        IReadOnlyCollection<string>? kioskIds,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}

/// <summary>One entry as the panel shows it; <paramref name="AdminName"/> is filled in from the accounts.</summary>
public sealed record AuditLogView(
    Guid Id,
    DateTime At,
    Guid? AdminUserId,
    string? AdminName,
    string ActorType,
    string Action,
    string EntityType,
    string? EntityId,
    string? Before,
    string? After,
    string? Ip,
    string? Reason,
    string? CorrelationId);

public sealed record AuditLogPage(IReadOnlyList<AuditLogView> Items, long Total, int Page, int PageSize);

/// <summary>The audit log, newest first, as far as the account's kiosk scope reaches.</summary>
public sealed record ListAuditLogsQuery(Guid AdminUserId, AuditLogFilter Filter, int Page = 1, int PageSize = 50)
    : IRequest<AuditLogPage>;

public sealed class AuditLogQueryHandlers : IRequestHandler<ListAuditLogsQuery, AuditLogPage>
{
    /// <summary>The kiosk entity's name in the log: what a scoped account may read.</summary>
    public const string KioskEntity = "Kiosk";

    private const int MostPerPage = 200;

    private readonly IAuditLogReader _log;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;

    public AuditLogQueryHandlers(IAuditLogReader log, IAdminUserRepository admins, IPermissionResolver permissions)
    {
        _log = log;
        _admins = admins;
        _permissions = permissions;
    }

    /// <inheritdoc cref="ListAuditLogsQuery" />
    public async Task<AuditLogPage> Handle(ListAuditLogsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, MostPerPage);

        if (request.Filter.From is { } from && request.Filter.To is { } to && from > to)
        {
            throw new Common.ValidationException([new Common.ValidationError("from", "audit.rangeInvalid")]);
        }

        // An account that looks after some kiosks reads what was done to them -
        // not to other kiosks, accounts, prices or settings.
        IReadOnlyCollection<string>? scope = effective.AllKiosks
            ? null
            : [.. effective.KioskIds.Select(id => id.ToString())];

        var (items, total) = await _log
            .ListAsync(request.Filter, scope, (page - 1) * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);

        var names = (await _admins.ListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(admin => admin.Id, admin => admin.FullName);

        return new AuditLogPage(
            [
                .. items.Select(entry => new AuditLogView(
                    entry.Id,
                    entry.CreatedAt,
                    entry.AdminUserId,
                    entry.AdminUserId is { } id ? names.GetValueOrDefault(id) : null,
                    entry.ActorType.ToString(),
                    entry.Action,
                    entry.EntityType,
                    entry.EntityId,
                    entry.Before,
                    entry.After,
                    entry.Ip,

                    // A reason the system wrote is a catalog key ("inventory.initialStock",
                    // T-323): read in the reader's language. A person's own words stay as written.
                    entry.Reason is { } reason && Messages.Has(reason) ? Messages.Write(new UserMessage(reason)) : entry.Reason,
                    entry.CorrelationId)),
            ],
            total,
            page,
            pageSize);
    }
}
