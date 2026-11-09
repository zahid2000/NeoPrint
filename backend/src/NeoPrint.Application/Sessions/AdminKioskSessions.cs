using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Notifications;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Sessions;

/// <summary>
/// The phone sessions open at a kiosk, as the panel lists them
/// (<c>GET admin/kiosks/{id}/sessions</c>, T-362): when each was opened and
/// runs out, and whose - by the customer's public id only.
/// </summary>
public sealed record GetAdminKioskSessionsQuery(Guid AdminUserId, Guid KioskId) : IRequest<IReadOnlyList<AdminKioskSessionView>>;

/// <summary>One open session at a kiosk (T-362).</summary>
/// <param name="UserId">For the customer's page, when the reader may open it.</param>
/// <param name="UserPublicId">The customer as the panel names them; never their name, phone or email here.</param>
public sealed record AdminKioskSessionView(Guid Id, Guid UserId, string UserPublicId, DateTime OpenedAt, DateTime ExpiresAt);

/// <summary>
/// Ends a session at a kiosk from the panel (T-362): the kiosk stops counting
/// the phone, the app is told it was closed by staff. Somebody says why
/// (audit). Ending one that is already over is not found.
/// </summary>
public sealed record CloseAdminKioskSessionCommand(Guid AdminUserId, Guid KioskId, Guid SessionId, string? Reason, string? Ip) : IRequest;

/// <inheritdoc cref="GetAdminKioskSessionsQuery" />
public sealed class AdminKioskSessionHandlers :
    IRequestHandler<GetAdminKioskSessionsQuery, IReadOnlyList<AdminKioskSessionView>>,
    IRequestHandler<CloseAdminKioskSessionCommand>
{
    private readonly IKioskSessionStore _sessions;
    private readonly IKioskRepository _kiosks;
    private readonly IUserRepository _users;
    private readonly IPermissionResolver _permissions;
    private readonly IKioskNotifier _kiosk;
    private readonly IUserNotifier _user;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<AdminKioskSessionHandlers> _logger;

    public AdminKioskSessionHandlers(
        IKioskSessionStore sessions,
        IKioskRepository kiosks,
        IUserRepository users,
        IPermissionResolver permissions,
        IKioskNotifier kiosk,
        IUserNotifier user,
        IAuditWriter audit,
        IClock clock,
        ILogger<AdminKioskSessionHandlers> logger)
    {
        _sessions = sessions;
        _kiosks = kiosks;
        _users = users;
        _permissions = permissions;
        _kiosk = kiosk;
        _user = user;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="GetAdminKioskSessionsQuery" />
    public async Task<IReadOnlyList<AdminKioskSessionView>> Handle(GetAdminKioskSessionsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureVisibleAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        var open = await _sessions.ListForKioskAsync(request.KioskId, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var views = new List<AdminKioskSessionView>(open.Count);
        foreach (var session in open.OrderBy(session => session.OpenedAt))
        {
            var user = await _users.FindByIdAsync(session.UserId, cancellationToken).ConfigureAwait(false);
            views.Add(new AdminKioskSessionView(session.Id, session.UserId, user?.PublicId ?? string.Empty, session.OpenedAt, session.ExpiresAt));
        }

        return views;
    }

    /// <inheritdoc cref="CloseAdminKioskSessionCommand" />
    public async Task Handle(CloseAdminKioskSessionCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureVisibleAsync(request.AdminUserId, request.KioskId, cancellationToken).ConfigureAwait(false);

        var reason = request.Reason?.Trim();
        if (reason is not { Length: >= 3 })
        {
            throw new ValidationException([new ValidationError("reason", "kioskSessions.reasonRequired")]);
        }

        if (reason.Length > 500)
        {
            reason = reason[..500];
        }

        // Only a session at this kiosk: an id from elsewhere reads as none.
        var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.KioskId != request.KioskId)
        {
            throw new NotFoundException("session");
        }

        // The one call that removed it announces it; a second press finds nothing.
        if (!await _sessions.RemoveAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("session");
        }

        var end = KioskSessionEnd.ClosedByStaff.ToString();
        await _kiosk.SessionClosedAsync(session.KioskId, session.Id, end, cancellationToken).ConfigureAwait(false);
        await _user.SessionClosedAsync(session.UserId, new UserSessionUpdate(session.Id, session.KioskId, end), cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(request.AdminUserId, "kiosks.session.close", "KioskSession", session.Id.ToString(), session.KioskId.ToString(), null, request.Ip, reason),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Session {SessionId} at kiosk {KioskId} was closed from the panel.", session.Id, session.KioskId);
    }

    private async Task EnsureVisibleAsync(Guid adminUserId, Guid kioskId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }
    }
}
