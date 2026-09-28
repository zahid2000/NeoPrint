using System.Security.Claims;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Reads the caller's identity from the current HTTP request.
/// </summary>
/// <remarks>
/// The four token types never overlap: a token carries exactly one of a user,
/// an administrator, a kiosk or a device awaiting activation. Reading them from
/// claims here keeps ASP.NET Core out of the handlers.
/// </remarks>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId => CallerClaims.UserId(Principal);

    public Guid? AdminUserId => CallerClaims.AdminUserId(Principal);

    public Guid? KioskId => CallerClaims.KioskId(Principal);

    public Guid? DeviceId => CallerClaims.DeviceId(Principal);

    /// <summary>
    /// The caller's address, preferring the forwarded header because the API
    /// sits behind the admin container's nginx.
    /// </summary>
    public string? Ip
    {
        get
        {
            var context = _accessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

            return string.IsNullOrWhiteSpace(forwarded)
                ? context.Connection.RemoteIpAddress?.ToString()
                : forwarded.Split(',')[0].Trim();
        }
    }

    /// <summary>The header the panel sends a reason in: URL-encoded, since headers are not UTF-8.</summary>
    public const string AuditReasonHeader = "X-Audit-Reason";

    /// <summary>The longest reason kept; anything past it is cut, not refused.</summary>
    public const int MaxAuditReasonLength = 500;

    /// <summary>
    /// The reason for this request, from <see cref="AuditReasonHeader"/>.
    /// </summary>
    /// <remarks>
    /// Only an administrator's: a reason is part of an admin action's record,
    /// and nobody else's request writes to the audit log.
    /// </remarks>
    public string? AuditReason
    {
        get
        {
            var raw = AdminUserId is null
                ? null
                : _accessor.HttpContext?.Request.Headers[AuditReasonHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string decoded;

            try
            {
                decoded = Uri.UnescapeDataString(raw).Trim();
            }
            catch (UriFormatException)
            {
                return null;
            }

            return decoded.Length == 0
                ? null
                : decoded.Length > MaxAuditReasonLength ? decoded[..MaxAuditReasonLength] : decoded;
        }
    }

}

/// <summary>
/// The caller's identity from a principal: the HTTP request's
/// (<see cref="CurrentUser"/>) or a hub connection's (<c>Context.User</c>).
/// </summary>
/// <remarks>
/// A hub reads <c>Context.User</c>, never the HTTP context accessor: with
/// long polling or server-sent events the hub runs outside the request that
/// carried the token, the accessor sees no request, and the connection would
/// be dropped as anonymous (docs/TASKS.md T-077).
/// </remarks>
public static class CallerClaims
{
    public static Guid? UserId(ClaimsPrincipal? principal) => IsTokenType(principal, TokenTypes.User)
        ? ParseGuid(principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal?.FindFirstValue("sub"))
        : null;

    public static Guid? AdminUserId(ClaimsPrincipal? principal) => IsTokenType(principal, TokenTypes.Admin)
        ? ParseGuid(principal?.FindFirstValue(NeoPrintClaims.AdminUserId))
        : null;

    public static Guid? KioskId(ClaimsPrincipal? principal) => ParseGuid(principal?.FindFirstValue(NeoPrintClaims.KioskId));

    public static Guid? DeviceId(ClaimsPrincipal? principal) => ParseGuid(principal?.FindFirstValue(NeoPrintClaims.DeviceId));

    private static bool IsTokenType(ClaimsPrincipal? principal, string expected)
        => string.Equals(principal?.FindFirstValue(NeoPrintClaims.TokenType), expected, StringComparison.Ordinal);

    private static Guid? ParseGuid(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed : null;
}
