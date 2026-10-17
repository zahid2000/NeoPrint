using Hangfire;
using Hangfire.Dashboard;

using MediatR;

using Microsoft.AspNetCore.DataProtection;

using NeoPrint.Application.Operations;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Hangfire's own panel at <c>/api/admin/hangfire</c> (T-167, T-215;
/// docs/DECISIONS.md 2026-10-11): SuperAdmin only, full control in every
/// environment, every opening and every action in the audit log.
/// </summary>
/// <remarks>
/// <para>
/// The panel is a page a browser opens, so it cannot carry the admin panel's
/// bearer token. The admin panel asks for a ticket instead
/// (<c>POST /api/admin/hangfire-session</c>, with the token): a cookie that
/// holds the account id, protected by ASP.NET data protection, valid for
/// <see cref="Lifetime"/>, sent only to the panel's own path, unreadable to
/// script (<c>HttpOnly</c>) and never sent by another site (<c>SameSite=Strict</c>).
/// </para>
/// <para>
/// The ticket only says who; on every request the account is asked again
/// whether it is still active and still a SuperAdmin.
/// </para>
/// </remarks>
public static class HangfireDashboard
{
    public const string Path = "/api/admin/hangfire";

    public const string CookieName = "neoprint_hangfire";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const string Purpose = "NeoPrint.HangfireTicket.v1";

    /// <summary>Panel requests that change nothing: the live counters.</summary>
    private static readonly string[] ReadOnlyPosts = ["/stats"];

    public static void IssueTicket(HttpContext context, Guid adminUserId, bool secure)
    {
        ArgumentNullException.ThrowIfNull(context);

        var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();

        context.Response.Cookies.Append(
            CookieName,
            protector.Protect(adminUserId.ToString("N"), Lifetime),
            new CookieOptions
            {
                Path = Path,
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Strict,
                MaxAge = Lifetime,
                IsEssential = true,
            });
    }

    /// <summary>The account the ticket names; null when there is none, or it is forged or old.</summary>
    public static Guid? ReadTicket(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Cookies.TryGetValue(CookieName, out var ticket) || string.IsNullOrEmpty(ticket))
        {
            return null;
        }

        try
        {
            var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();

            return Guid.TryParseExact(protector.Unprotect(ticket), "N", out var id) ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The panel, behind the ticket, with the audit of what is done in it.</summary>
    public static void UseNeoPrintHangfireDashboard(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Building the panel resolves Hangfire's global configuration, which is process-wide:
        // a second, short-lived host (the integration tests' HTTP-only one) must not take it
        // over with services it disposes (docs/DECISIONS.md 2026-09-30, 2026-10-17).
        if (string.Equals(app.Configuration["Hangfire:Dashboard"], "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(Path, out var rest)
                       && HttpMethods.IsPost(context.Request.Method)
                       && !ReadOnlyPosts.Contains(rest.Value, StringComparer.OrdinalIgnoreCase),
            branch => branch.Use(AuditAsync));

        app.UseHangfireDashboard(Path, new DashboardOptions
        {
            Authorization = [],
            AsyncAuthorization = [new TicketAuthorization()],
            IsReadOnlyFunc = _ => false,
            DisplayStorageConnectionString = false,
            DashboardTitle = "NeoPrint — Hangfire",
            AppPath = "/",
        });
    }

    private static async Task AuditAsync(HttpContext context, RequestDelegate next)
    {
        var admin = ReadTicket(context);
        var jobs = new List<string>();

        if (admin is not null && context.Request.HasFormContentType)
        {
            // Read before the panel does; buffered, so it can read it again.
            context.Request.EnableBuffering();
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            jobs.AddRange(form["jobs[]"].Where(id => !string.IsNullOrEmpty(id)).Select(id => id!));
            context.Request.Body.Position = 0;
        }

        await next(context).ConfigureAwait(false);

        if (admin is { } id && context.Response.StatusCode < 400)
        {
            context.Request.Path.StartsWithSegments(Path, out var action);
            await context.RequestServices.GetRequiredService<ISender>()
                .Send(new RecordHangfireActionCommand(id, action.Value ?? "/", jobs, context.Connection.RemoteIpAddress?.ToString()), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private sealed class TicketAuthorization : IDashboardAsyncAuthorizationFilter
    {
        public async Task<bool> AuthorizeAsync(DashboardContext context)
        {
            var http = context.GetHttpContext();

            return ReadTicket(http) is { } admin
                   && await http.RequestServices.GetRequiredService<ISender>()
                       .Send(new CanUseHangfireQuery(admin), http.RequestAborted)
                       .ConfigureAwait(false);
        }
    }
}
