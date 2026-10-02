using System.Globalization;
using System.Text.Json;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Sessions;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.Sessions;

/// <inheritdoc cref="IKioskSessionStore" />
/// <remarks>
/// <para>
/// <c>session:{id}</c> holds the session and expires with it (plan section 5);
/// <c>session:user:{userId}</c> points at the user's one session and expires
/// with it too.
/// </para>
/// <para>
/// An expired key leaves nothing to say whose session it was, so each session
/// is also a member of <c>sessions:expiring</c> (scored by when it ends) with
/// its details in <c>sessions:info</c>. Whoever removes the member from the
/// set - the user closing it, or the expiry run - is the one who announces the
/// end: <c>ZREM</c> answers 1 to exactly one caller.
/// </para>
/// </remarks>
public sealed class RedisKioskSessionStore : IKioskSessionStore
{
    private const string Expiring = "sessions:expiring";
    private const string Info = "sessions:info";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly IClock _clock;

    public RedisKioskSessionStore(IConnectionMultiplexer redis, IClock clock)
    {
        _redis = redis;
        _clock = clock;
    }

    public async Task SaveAsync(KioskSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var ttl = session.ExpiresAt - _clock.UtcNow;

        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        var json = JsonSerializer.Serialize(session, Json);
        var id = Id(session.Id);
        var transaction = _redis.GetDatabase().CreateTransaction();

        _ = transaction.StringSetAsync(SessionKey(session.Id), json, ttl);
        _ = transaction.StringSetAsync(UserKey(session.UserId), id, ttl);
        _ = transaction.SortedSetAddAsync(Expiring, id, Score(session.ExpiresAt));
        _ = transaction.HashSetAsync(Info, id, json);

        await transaction.ExecuteAsync().ConfigureAwait(false);
    }

    public async Task<KioskSession?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var value = await _redis.GetDatabase().StringGetAsync(SessionKey(id)).ConfigureAwait(false);

        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<KioskSession>(value.ToString(), Json);
    }

    public async Task<KioskSession?> FindForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var id = await _redis.GetDatabase().StringGetAsync(UserKey(userId)).ConfigureAwait(false);

        return !id.IsNullOrEmpty && Guid.TryParseExact(id.ToString(), "N", out var sessionId)
            ? await FindAsync(sessionId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var database = _redis.GetDatabase();

        if (!await database.SortedSetRemoveAsync(Expiring, Id(id)).ConfigureAwait(false))
        {
            // Somebody else ended it first: they announce it.
            return false;
        }

        await ForgetAsync(database, id).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<KioskSession>> TakeExpiredAsync(DateTime now, int max, CancellationToken cancellationToken = default)
    {
        var database = _redis.GetDatabase();
        var due = await database
            .SortedSetRangeByScoreAsync(Expiring, double.NegativeInfinity, Score(now), take: max)
            .ConfigureAwait(false);

        var taken = new List<KioskSession>(due.Length);

        foreach (var member in due)
        {
            // Extended since it was read (an order was placed in it): its score
            // moved on, and it is not over after all.
            if (await database.SortedSetScoreAsync(Expiring, member).ConfigureAwait(false) is not { } score || score > Score(now))
            {
                continue;
            }

            if (!await database.SortedSetRemoveAsync(Expiring, member).ConfigureAwait(false)
                || !Guid.TryParseExact(member.ToString(), "N", out var id))
            {
                continue;
            }

            var info = await database.HashGetAsync(Info, member).ConfigureAwait(false);
            await ForgetAsync(database, id).ConfigureAwait(false);

            if (!info.IsNullOrEmpty && JsonSerializer.Deserialize<KioskSession>(info.ToString(), Json) is { } session)
            {
                taken.Add(session);
            }
        }

        return taken;
    }

    /// <summary>The session and its pointers go; the user's pointer only if it still points here.</summary>
    private static async Task ForgetAsync(IDatabase database, Guid id)
    {
        var info = await database.HashGetAsync(Info, Id(id)).ConfigureAwait(false);
        var transaction = database.CreateTransaction();

        _ = transaction.KeyDeleteAsync(SessionKey(id));
        _ = transaction.HashDeleteAsync(Info, Id(id));
        await transaction.ExecuteAsync().ConfigureAwait(false);

        if (!info.IsNullOrEmpty && JsonSerializer.Deserialize<KioskSession>(info.ToString(), Json) is { } session)
        {
            var pointer = database.CreateTransaction();
            pointer.AddCondition(Condition.StringEqual(UserKey(session.UserId), Id(id)));
            _ = pointer.KeyDeleteAsync(UserKey(session.UserId));
            await pointer.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private static string Id(Guid id) => id.ToString("N");

    private static double Score(DateTime at) => new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static RedisKey SessionKey(Guid id) => $"session:{Id(id)}";

    private static RedisKey UserKey(Guid userId) => string.Create(CultureInfo.InvariantCulture, $"session:user:{userId:N}");
}
