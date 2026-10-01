using NeoPrint.Application.Customers;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.Auth;

/// <summary>Blocked customers whose access tokens must stop working now, held in Redis.</summary>
public sealed class RedisUserBlocklist : IUserBlocklist
{
    private readonly IConnectionMultiplexer _redis;

    public RedisUserBlocklist(IConnectionMultiplexer redis) => _redis = redis;

    public Task BlockAsync(Guid userId, TimeSpan duration, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().StringSetAsync(Key(userId), "1", duration);

    public Task UnblockAsync(Guid userId, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyDeleteAsync(Key(userId));

    public Task<bool> IsBlockedAsync(Guid userId, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyExistsAsync(Key(userId));

    private static RedisKey Key(Guid userId) => $"user:blocked:{userId}";
}
