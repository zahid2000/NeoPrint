using System.Security.Cryptography;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Application.Kiosks;

/// <summary><c>operator_kiosk_logins</c> (T-392).</summary>
public interface IOperatorKioskLoginRepository
{
    Task AddAsync(OperatorKioskLogin login, CancellationToken cancellationToken = default);

    Task<OperatorKioskLogin?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<OperatorKioskLogin?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    Task UpdateAsync(OperatorKioskLogin login, CancellationToken cancellationToken = default);
}

/// <summary>The kiosk asks for a QR to show on its admin corner (T-392).</summary>
public sealed record StartOperatorKioskLoginCommand(Guid DeviceId) : IRequest<OperatorKioskLoginStarted>;

/// <param name="Qr">What the QR says: <c>neoprint://kiosk-login/{code}</c>.</param>
public sealed record OperatorKioskLoginStarted(Guid Id, string Qr, DateTime ExpiresAt);

/// <summary>The kiosk looks whether an operator confirmed yet.</summary>
public sealed record GetOperatorKioskLoginQuery(Guid DeviceId, Guid LoginId) : IRequest<OperatorKioskLoginView>;

/// <param name="Status">Pending, Approved, Ended, or Expired (the QR or the menu's time is up).</param>
/// <param name="CanSelectPrinter">The operator may choose the printer here (<c>printers.select</c>).</param>
/// <param name="CanChangeQueue">The operator may set the queue panel's size (<c>kiosks.limits.manage</c>).</param>
/// <param name="CanPause">The operator may stop and restart taking orders (<c>kiosks.commands.pause</c>).</param>
public sealed record OperatorKioskLoginView(
    Guid Id,
    string Status,
    string? OperatorName,
    DateTime ExpiresAt,
    bool CanSelectPrinter,
    bool CanChangeQueue,
    bool CanPause);

/// <summary>The operator read the kiosk's QR in the operator panel and confirms (T-392).</summary>
public sealed record ApproveOperatorKioskLoginCommand(Guid AdminUserId, string Code, string? Ip)
    : IRequest<OperatorKioskLoginApproved>;

public sealed record OperatorKioskLoginApproved(Guid KioskId, string KioskCode, string KioskName);

/// <summary>What the operator does from the kiosk's menu.</summary>
public enum OperatorKioskAction
{
    SelectPrinter = 0,
    SetQueueLimit = 1,
    Pause = 2,
    Resume = 3,
}

/// <summary>
/// A change made from the kiosk's admin menu (T-392): carried out under the
/// operator's account, with the permissions and kiosk scope it has now, and
/// audited in its name - as if the operator had made it in the panel.
/// </summary>
public sealed record OperatorKioskActionCommand(
    Guid DeviceId, Guid LoginId, OperatorKioskAction Action, string? Printer, int? Limit, string? Ip) : IRequest;

/// <summary>The menu was closed: nothing more in the operator's name.</summary>
public sealed record EndOperatorKioskLoginCommand(Guid DeviceId, Guid LoginId) : IRequest;

public sealed class OperatorKioskLoginHandlers :
    IRequestHandler<StartOperatorKioskLoginCommand, OperatorKioskLoginStarted>,
    IRequestHandler<GetOperatorKioskLoginQuery, OperatorKioskLoginView>,
    IRequestHandler<ApproveOperatorKioskLoginCommand, OperatorKioskLoginApproved>,
    IRequestHandler<OperatorKioskActionCommand>,
    IRequestHandler<EndOperatorKioskLoginCommand>
{
    /// <summary>The scheme the operator panel's scanner looks for.</summary>
    public const string QrPrefix = "neoprint://kiosk-login/";

    private readonly IOperatorKioskLoginRepository _logins;
    private readonly IKioskDeviceRepository _devices;
    private readonly IKioskRepository _kiosks;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly ISender _sender;
    private readonly IClock _clock;

    public OperatorKioskLoginHandlers(
        IOperatorKioskLoginRepository logins,
        IKioskDeviceRepository devices,
        IKioskRepository kiosks,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IAuditWriter audit,
        ISender sender,
        IClock clock)
    {
        _logins = logins;
        _devices = devices;
        _kiosks = kiosks;
        _admins = admins;
        _permissions = permissions;
        _audit = audit;
        _sender = sender;
        _clock = clock;
    }

    public async Task<OperatorKioskLoginStarted> Handle(StartOperatorKioskLoginCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kioskId = await KioskOfAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var login = OperatorKioskLogin.Start(kioskId, code, _clock.UtcNow);

        await _logins.AddAsync(login, cancellationToken).ConfigureAwait(false);

        return new OperatorKioskLoginStarted(login.Id, QrPrefix + code, login.ExpiresAt);
    }

    public async Task<OperatorKioskLoginView> Handle(GetOperatorKioskLoginQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var login = await OwnAsync(request.DeviceId, request.LoginId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var status = login.Status switch
        {
            OperatorKioskLoginStatus.Pending when now >= login.ExpiresAt => "Expired",
            OperatorKioskLoginStatus.Approved when now >= login.ExpiresAt => "Expired",
            var other => other.ToString(),
        };

        if (status != nameof(OperatorKioskLoginStatus.Approved) || login.OperatorId is not { } operatorId)
        {
            return new OperatorKioskLoginView(login.Id, status, null, login.ExpiresAt, false, false, false);
        }

        // What the operator may do here now, not when they confirmed.
        var effective = await _permissions.ResolveAsync(operatorId, cancellationToken).ConfigureAwait(false);
        var sees = effective.CanSee(login.KioskId);

        return new OperatorKioskLoginView(
            login.Id,
            status,
            login.OperatorName,
            login.ExpiresAt,
            sees && effective.Has(Permissions.PrintersSelect),
            sees && effective.Has(Permissions.KioskLimitsManage),
            sees && effective.Has(Permissions.KioskCommandsPause));
    }

    public async Task<OperatorKioskLoginApproved> Handle(ApproveOperatorKioskLoginCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var code = (request.Code ?? string.Empty).Trim();
        if (code.StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase))
        {
            code = code[QrPrefix.Length..];
        }

        var login = await _logins.FindByCodeAsync(code.ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("operatorLogin");

        // A kiosk outside the operator's scope is "not found", as everywhere in the panel.
        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var kiosk = await _kiosks.FindByIdAsync(login.KioskId, cancellationToken).ConfigureAwait(false);
        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("operatorLogin");
        }

        var admin = await _admins.FindByIdAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("operatorLogin");

        var now = _clock.UtcNow;
        login.Approve(admin.Id, string.IsNullOrWhiteSpace(admin.FullName) ? admin.Email : admin.FullName, now);
        await _logins.UpdateAsync(login, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(admin.Id, "kiosks.operator-login", "Kiosk", kiosk.Id.ToString(), null, null, request.Ip, Reason: null),
                cancellationToken)
            .ConfigureAwait(false);

        return new OperatorKioskLoginApproved(kiosk.Id, kiosk.Code, kiosk.Name);
    }

    public async Task Handle(OperatorKioskActionCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var login = await OwnAsync(request.DeviceId, request.LoginId, cancellationToken).ConfigureAwait(false);
        if (!login.IsOpen(_clock.UtcNow) || login.OperatorId is not { } operatorId)
        {
            throw new DomainException("operatorLogin.expired");
        }

        var effective = await _permissions.ResolveAsync(operatorId, cancellationToken).ConfigureAwait(false);
        var needed = request.Action switch
        {
            OperatorKioskAction.SelectPrinter => Permissions.PrintersSelect,
            OperatorKioskAction.SetQueueLimit => Permissions.KioskLimitsManage,
            _ => Permissions.KioskCommandsPause,
        };

        if (!effective.CanSee(login.KioskId) || !effective.Has(needed))
        {
            throw new DomainException("operatorLogin.notAllowed");
        }

        // The panel's own commands, in the operator's name: the same checks, the same audit.
        switch (request.Action)
        {
            case OperatorKioskAction.SelectPrinter:
                await _sender.Send(new SelectKioskPrinterCommand(operatorId, login.KioskId, request.Printer ?? string.Empty), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case OperatorKioskAction.SetQueueLimit:
                await _sender.Send(new SetKioskQueueLimitCommand(login.KioskId, request.Limit ?? 0, operatorId, request.Ip), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case OperatorKioskAction.Pause:
                await _sender.Send(new SendKioskCommandCommand(operatorId, login.KioskId, KioskCommandType.Pause, request.Ip), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case OperatorKioskAction.Resume:
                await _sender.Send(new SendKioskCommandCommand(operatorId, login.KioskId, KioskCommandType.Resume, request.Ip), cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    public async Task Handle(EndOperatorKioskLoginCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var login = await OwnAsync(request.DeviceId, request.LoginId, cancellationToken).ConfigureAwait(false);
        login.End(_clock.UtcNow);
        await _logins.UpdateAsync(login, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> KioskOfAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _devices.FindByIdAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return device?.KioskId ?? throw new NotFoundException("kiosk");
    }

    /// <summary>A login of this device's kiosk; another kiosk's is "not found".</summary>
    private async Task<OperatorKioskLogin> OwnAsync(Guid deviceId, Guid loginId, CancellationToken cancellationToken)
    {
        var kioskId = await KioskOfAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var login = await _logins.FindByIdAsync(loginId, cancellationToken).ConfigureAwait(false);

        return login is not null && login.KioskId == kioskId ? login : throw new NotFoundException("operatorLogin");
    }
}
