using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Notifications;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Sessions;

/// <summary>
/// The app joined to one kiosk for a while (plan sections 5 and 9.3:
/// <c>session:{id}</c> in Redis → <c>{UserId, KioskId, ExpiresAt}</c>).
/// </summary>
/// <remarks>
/// Not a business record: nothing is paid or printed by a session itself, it
/// only says which kiosk the app's next order goes to. Losing Redis loses the
/// open sessions and the user opens one again; orders already placed are in
/// MongoDB (docs/FAILURE_RECOVERY.md).
/// </remarks>
public sealed record KioskSession(Guid Id, Guid UserId, Guid KioskId, DateTime OpenedAt, DateTime ExpiresAt);

/// <summary>Where sessions live, with their time to live (Redis).</summary>
public interface IKioskSessionStore
{
    /// <summary>Writes it, replacing the same id, to expire at its <see cref="KioskSession.ExpiresAt"/>.</summary>
    Task SaveAsync(KioskSession session, CancellationToken cancellationToken = default);

    /// <summary>The session while it lasts; null once it expired or was closed.</summary>
    Task<KioskSession?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The user's open session, if any: one per user.</summary>
    Task<KioskSession?> FindForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Ends it. True only for the one call that ended it, so its end is announced once.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sessions whose time ran out, each handed to one caller only, so the
    /// kiosk and the app hear of each expiry once.
    /// </summary>
    Task<IReadOnlyList<KioskSession>> TakeExpiredAsync(DateTime now, int max, CancellationToken cancellationToken = default);
}

/// <summary>Why a session ended, as the kiosk and the app are told.</summary>
public enum KioskSessionEnd
{
    /// <summary>The user closed it.</summary>
    Closed,

    /// <summary><c>KioskSession.TtlMinutes</c> passed without anything done in it.</summary>
    Expired,

    /// <summary>The user opened one at another kiosk.</summary>
    Replaced,
}

/// <summary>"Sessiya aç" (plan section 9.3, step 1): by the kiosk's id or code, from the search or its QR.</summary>
public sealed record OpenKioskSessionCommand(Guid UserId, string Kiosk) : IRequest<KioskSessionView>;

public sealed record GetKioskSessionQuery(Guid UserId, Guid SessionId) : IRequest<KioskSessionView>;

/// <summary>The app's own session, or none (<c>GET sessions/current</c>).</summary>
public sealed record GetCurrentKioskSessionQuery(Guid UserId) : IRequest<KioskSessionView?>;

/// <summary>Ends the user's session; ending one that is already over is no error.</summary>
public sealed record CloseKioskSessionCommand(Guid UserId, Guid SessionId) : IRequest;

/// <summary>Announces the sessions whose time ran out (every minute).</summary>
public sealed record ExpireKioskSessionsCommand : IRequest<int>;

/// <param name="TtlMinutes">How long it lasts without anything done in it: each order extends it.</param>
public sealed record KioskSessionView(
    Guid Id, Guid KioskId, string KioskCode, string KioskName, DateTime ExpiresAt, int TtlMinutes);

/// <summary>A session that is open and the caller's, extended by what is being done in it.</summary>
public interface IKioskSessionAccess
{
    /// <exception cref="NotFoundException">Not open any more, or not the caller's.</exception>
    Task<KioskSession> UseAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="OpenKioskSessionCommand" />
public sealed class OpenKioskSessionValidator : IValidator<OpenKioskSessionCommand>
{
    public IEnumerable<ValidationError> Validate(OpenKioskSessionCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Kiosk) || request.Kiosk.Trim().Length > 64)
        {
            yield return new ValidationError("kiosk", "sessions.kioskRequired");
        }
    }
}

public sealed class KioskSessionHandlers :
    IRequestHandler<OpenKioskSessionCommand, KioskSessionView>,
    IRequestHandler<GetKioskSessionQuery, KioskSessionView>,
    IRequestHandler<GetCurrentKioskSessionQuery, KioskSessionView?>,
    IRequestHandler<CloseKioskSessionCommand>,
    IRequestHandler<ExpireKioskSessionsCommand, int>,
    IKioskSessionAccess
{
    /// <summary>Expired sessions announced per run; the next run takes the rest.</summary>
    private const int ExpiryBatch = 200;

    private readonly IKioskSessionStore _sessions;
    private readonly IKioskRepository _kiosks;
    private readonly IKioskNotifier _kiosk;
    private readonly IUserNotifier _user;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<KioskSessionHandlers> _logger;

    public KioskSessionHandlers(
        IKioskSessionStore sessions,
        IKioskRepository kiosks,
        IKioskNotifier kiosk,
        IUserNotifier user,
        ISettingsReader settings,
        IClock clock,
        ILogger<KioskSessionHandlers> logger)
    {
        _sessions = sessions;
        _kiosks = kiosks;
        _kiosk = kiosk;
        _user = user;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="OpenKioskSessionCommand" />
    /// <remarks>
    /// Opening again at the same kiosk is the same session, made longer - a
    /// double tap or a retried request does not open two. Opening at another
    /// kiosk ends the first: the app talks to one kiosk at a time.
    /// </remarks>
    public async Task<KioskSessionView> Handle(OpenKioskSessionCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kiosk = await KioskAsync(request.Kiosk.Trim(), cancellationToken).ConfigureAwait(false);

        if (kiosk.Status != KioskStatus.Online)
        {
            // A session is for printing now: a paused or offline kiosk would
            // take the order and leave it waiting.
            throw new DomainException("sessions.kioskNotTakingOrders");
        }

        var ttl = await TtlAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var current = await _sessions.FindForUserAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        if (current is not null && current.KioskId == kiosk.Id)
        {
            var extended = current with { ExpiresAt = now.Add(ttl) };
            await _sessions.SaveAsync(extended, cancellationToken).ConfigureAwait(false);
            await _kiosk.SessionOpenedAsync(kiosk.Id, extended.Id, extended.ExpiresAt, cancellationToken).ConfigureAwait(false);
            return View(extended, kiosk, ttl);
        }

        if (current is not null)
        {
            await EndAsync(current, KioskSessionEnd.Replaced, cancellationToken).ConfigureAwait(false);
        }

        var session = new KioskSession(Guid.NewGuid(), request.UserId, kiosk.Id, now, now.Add(ttl));
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        // A small notice on the kiosk's screen (plan section 9.3): that a phone
        // joined, never who - the screen is in a public place.
        await _kiosk.SessionOpenedAsync(kiosk.Id, session.Id, session.ExpiresAt, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Session {SessionId} opened at kiosk {KioskId}, for {Minutes} min.", session.Id, kiosk.Id, ttl.TotalMinutes);

        return View(session, kiosk, ttl);
    }

    /// <inheritdoc cref="GetKioskSessionQuery" />
    public async Task<KioskSessionView> Handle(GetKioskSessionQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await OwnAsync(request.UserId, request.SessionId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(session.KioskId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("session");

        return View(session, kiosk, await TtlAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="GetCurrentKioskSessionQuery" />
    public async Task<KioskSessionView?> Handle(GetCurrentKioskSessionQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.FindForUserAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        if (session is null || session.ExpiresAt <= _clock.UtcNow)
        {
            return null;
        }

        var kiosk = await _kiosks.FindByIdAsync(session.KioskId, cancellationToken).ConfigureAwait(false);

        return kiosk is null ? null : View(session, kiosk, await TtlAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="CloseKioskSessionCommand" />
    public async Task Handle(CloseKioskSessionCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false);

        // Already over, or never this user's: nothing to end, and nothing to
        // tell about a session of somebody else's.
        if (session is null || session.UserId != request.UserId)
        {
            return;
        }

        await EndAsync(session, KioskSessionEnd.Closed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ExpireKioskSessionsCommand" />
    public async Task<int> Handle(ExpireKioskSessionsCommand request, CancellationToken cancellationToken)
    {
        var expired = await _sessions.TakeExpiredAsync(_clock.UtcNow, ExpiryBatch, cancellationToken).ConfigureAwait(false);

        foreach (var session in expired)
        {
            await AnnounceAsync(session, KioskSessionEnd.Expired, cancellationToken).ConfigureAwait(false);
        }

        if (expired.Count > 0)
        {
            _logger.LogInformation("{Count} kiosk session(s) expired.", expired.Count);
        }

        return expired.Count;
    }

    /// <inheritdoc />
    public async Task<KioskSession> UseAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await OwnAsync(userId, sessionId, cancellationToken).ConfigureAwait(false);

        // Something done in it: the inactivity clock starts again.
        var extended = session with { ExpiresAt = _clock.UtcNow.Add(await TtlAsync(cancellationToken).ConfigureAwait(false)) };
        await _sessions.SaveAsync(extended, cancellationToken).ConfigureAwait(false);

        return extended;
    }

    private async Task<KioskSession> OwnAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // Another user's session reads as none: its id says nothing to them.
        return session is not null && session.UserId == userId && session.ExpiresAt > _clock.UtcNow
            ? session
            : throw new NotFoundException("session");
    }

    private async Task EndAsync(KioskSession session, KioskSessionEnd end, CancellationToken cancellationToken)
    {
        if (await _sessions.RemoveAsync(session.Id, cancellationToken).ConfigureAwait(false))
        {
            await AnnounceAsync(session, end, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnounceAsync(KioskSession session, KioskSessionEnd end, CancellationToken cancellationToken)
    {
        await _kiosk.SessionClosedAsync(session.KioskId, session.Id, end.ToString(), cancellationToken).ConfigureAwait(false);
        await _user.SessionClosedAsync(session.UserId, new UserSessionUpdate(session.Id, session.KioskId, end.ToString()), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Session {SessionId} at kiosk {KioskId} ended: {End}.", session.Id, session.KioskId, end);
    }

    /// <summary>A kiosk a customer can go to, by id or code; any other is not found.</summary>
    private async Task<Kiosk> KioskAsync(string idOrCode, CancellationToken cancellationToken)
    {
        var kiosk = Guid.TryParse(idOrCode, out var id)
            ? await _kiosks.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : await _kiosks.FindByCodeAsync(idOrCode.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);

        return kiosk is not null && kiosk.Status is not (KioskStatus.NotActivated or KioskStatus.NotConfigured or KioskStatus.Disabled)
            ? kiosk
            : throw new NotFoundException("kiosk");
    }

    private async Task<TimeSpan> TtlAsync(CancellationToken cancellationToken)
        => TimeSpan.FromMinutes(Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.KioskSessionTtlMinutes, 10, cancellationToken).ConfigureAwait(false), 1, 120));

    private static KioskSessionView View(KioskSession session, Kiosk kiosk, TimeSpan ttl)
        => new(session.Id, kiosk.Id, kiosk.Code, kiosk.Name, session.ExpiresAt, (int)ttl.TotalMinutes);
}
