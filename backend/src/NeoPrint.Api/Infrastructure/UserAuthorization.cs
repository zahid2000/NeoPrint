using Microsoft.AspNetCore.Authorization;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Customers;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Requires that the customer a user token names has not been blocked since
/// the token was issued (plan section 8A.3: blocking takes effect now).
/// </summary>
public sealed class UserIsActiveRequirement : IAuthorizationRequirement;

/// <summary>
/// Confirms a user token against the blocklist; when Redis cannot answer, the
/// account itself in MongoDB does - the source of truth, a little slower.
/// </summary>
public sealed class UserAuthorizationHandler : AuthorizationHandler<UserIsActiveRequirement>
{
    private readonly IUserBlocklist _blocklist;
    private readonly IUserRepository _users;
    private readonly ILogger<UserAuthorizationHandler> _logger;

    public UserAuthorizationHandler(IUserBlocklist blocklist, IUserRepository users, ILogger<UserAuthorizationHandler> logger)
    {
        _blocklist = blocklist;
        _users = users;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, UserIsActiveRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (CallerClaims.UserId(context.User) is not { } userId)
        {
            return;
        }

        bool blocked;

        try
        {
            blocked = await _blocklist.IsBlockedAsync(userId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is StackExchange.Redis.RedisException or TimeoutException)
        {
            _logger.LogWarning("The user blocklist could not be read; checking the account itself.");
            blocked = (await _users.FindByIdAsync(userId).ConfigureAwait(false))?.CanSignIn != true;
        }

        if (!blocked)
        {
            context.Succeed(requirement);
            return;
        }

        _logger.LogWarning("User {UserId} presented a token for a blocked account.", userId);
    }
}
