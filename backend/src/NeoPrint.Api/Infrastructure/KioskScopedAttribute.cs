using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// The record named in the route belongs to a kiosk: an account that looks
/// after some kiosks reads and acts on its own kiosks' records only (T-301).
/// </summary>
/// <remarks>
/// Another kiosk's record is not found (404), as everywhere else; so is one
/// that belongs to no kiosk - a top-up, an app order - for such an account,
/// which is the rule the lists apply. A record that does not exist is left to
/// the action, which says so in its own words.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KioskScopedAttribute : TypeFilterAttribute
{
    public KioskScopedAttribute(RecordKind kind, string routeKey = "id")
        : base(typeof(KioskScopeFilter))
        => Arguments = [kind, routeKey];
}

public sealed class KioskScopeFilter : IAsyncActionFilter
{
    private readonly RecordKind _kind;
    private readonly string _routeKey;
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionResolver _permissions;
    private readonly IKioskOwnershipReader _owners;

    public KioskScopeFilter(RecordKind kind, string routeKey, ICurrentUser currentUser, IPermissionResolver permissions, IKioskOwnershipReader owners)
    {
        _kind = kind;
        _routeKey = routeKey;
        _currentUser = currentUser;
        _permissions = permissions;
        _owners = owners;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var cancellation = context.HttpContext.RequestAborted;

        if (_currentUser.AdminUserId is { } adminId
            && context.RouteData.Values.TryGetValue(_routeKey, out var raw)
            && Guid.TryParse(raw?.ToString(), out var id))
        {
            var effective = await _permissions.ResolveAsync(adminId, cancellation).ConfigureAwait(false);

            if (!effective.AllKiosks)
            {
                var owner = await _owners.KioskOfAsync(_kind, id, cancellation).ConfigureAwait(false);

                if (owner.Found && (owner.KioskId is not { } kioskId || !effective.CanSee(kioskId)))
                {
                    throw new NotFoundException(NotFoundName(_kind));
                }
            }
        }

        await next().ConfigureAwait(false);
    }

    private static string NotFoundName(RecordKind kind) => kind switch
    {
        RecordKind.Payment => "payment",
        RecordKind.Refund => "refund",
        RecordKind.FiscalReceipt => "fiscalReceipt",
        RecordKind.Receipt => "receipt",
        RecordKind.SupportClaim => "claim",
        _ => "record",
    };
}
