using System.Collections.Concurrent;
using System.Text.Json;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;

using StackExchange.Redis;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// The limits on a kiosk's own anonymous calls - registering, asking for a
/// challenge, exchanging it for a token (T-309).
/// </summary>
/// <remarks>
/// <para>
/// Several kiosks of one place share one public address behind its router,
/// and a golden image is provisioned onto one machine after another (M19).
/// Counted by address, the tenth registration of the hour - or the sixth
/// kiosk signing in after a power cut - was refused. Now:
/// </para>
/// <list type="bullet">
/// <item>a registration with an enrollment token that is good counts against
/// that token (each kiosk has its own), one from a key already registered -
/// against that device and the address (T-325), any other - against the address;</item>
/// <item>a challenge and a token count against the device and the address
/// together, so kiosks behind one router do not hold each other up and nobody
/// elsewhere can use up a kiosk's quota by knowing its id.</item>
/// </list>
/// <para>
/// The counts are kept in Redis: a restart of the API does not reset them,
/// and every instance counts the same. Without Redis they are kept in memory -
/// per instance and gone with a restart, which the log says.
/// </para>
/// </remarks>
public sealed class DeviceRequestLimits
{
    private const int MaxBody = 64 * 1024;

    private static readonly ConcurrentDictionary<string, (DateTime WindowEnds, int Count)> Fallback = new();

    private readonly RequestDelegate _next;
    private readonly RateLimitSettings _settings;
    private readonly ILogger<DeviceRequestLimits> _logger;

    public DeviceRequestLimits(RequestDelegate next, RateLimitSettings settings, ILogger<DeviceRequestLimits> logger)
    {
        _next = next;
        _settings = settings;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? string.Empty;

        if (_settings.Disabled
            || !HttpMethods.IsPost(context.Request.Method)
            || path is not ("/api/devices/register" or "/api/devices/challenge" or "/api/devices/token"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var body = await ReadAsync(context).ConfigureAwait(false);
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (key, limit, window) = path == "/api/devices/register"
            ? await RegistrationAsync(context, body, address).ConfigureAwait(false)
            : await SignInAsync(context, body, address).ConfigureAwait(false);

        var (allowed, secondsLeft) = await CountAsync(context, key, limit, window).ConfigureAwait(false);

        if (!allowed)
        {
            await RequestLimits.WriteTooManyAsync(context, secondsLeft, exact: true, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task<(string Key, int Limit, TimeSpan Window)> RegistrationAsync(HttpContext context, JsonElement? body, string address)
    {
        if (body is { } json && Property(json, "enrollmentToken") is { ValueKind: JsonValueKind.String } raw && raw.GetString() is { Length: > 0 } token)
        {
            var services = context.RequestServices;
            var found = await services.GetRequiredService<IEnrollmentTokenRepository>()
                .FindByHashAsync(services.GetRequiredService<ISecretHasher>().HashToken(token.Trim()), context.RequestAborted)
                .ConfigureAwait(false);

            if (found is not null && found.IsUsable(DateTime.UtcNow))
            {
                // A token for several new kiosks - a golden image's clones - gets
                // room for each of them to try twice; a single one keeps the plain limit (T-327).
                return ($"device-register:enroll:{found.Id:N}", Math.Max(_settings.EnrollmentTokenPerHour, found.MaxUses * 2), TimeSpan.FromHours(1));
            }
        }

        // A kiosk that is registered already says so again on every start - after
        // an update, a power cut (T-325). Counted by the address, the eleventh kiosk
        // of one place coming back in the update window was refused, failed its
        // health check and was rolled back. Like signing in, it counts against
        // the device and the address together; an unknown key still against the address.
        if (body is { } registration
            && Property(registration, "publicKey") is { ValueKind: JsonValueKind.String } key
            && key.GetString() is { Length: > 0 and <= 4096 } publicKey
            && await context.RequestServices.GetRequiredService<IKioskDeviceRepository>()
                .FindByPublicKeyAsync(publicKey, context.RequestAborted).ConfigureAwait(false) is { } device)
        {
            return ($"device-register:device:{device.Id:N}:{address}", _settings.DeviceRegisterPerHour, TimeSpan.FromHours(1));
        }

        return ($"device-register:ip:{address}", _settings.DeviceRegisterPerHour, TimeSpan.FromHours(1));
    }

    private async Task<(string Key, int Limit, TimeSpan Window)> SignInAsync(HttpContext context, JsonElement? body, string address)
    {
        if (body is { } json
            && Property(json, "deviceId") is { ValueKind: JsonValueKind.String } raw
            && Guid.TryParse(raw.GetString(), out var deviceId)
            && await context.RequestServices.GetRequiredService<IKioskDeviceRepository>()
                .FindByIdAsync(deviceId, context.RequestAborted).ConfigureAwait(false) is not null)
        {
            return ($"device-auth:{deviceId:N}:{address}", _settings.AuthPerMinute, TimeSpan.FromMinutes(1));
        }

        return ($"device-auth:ip:{address}", _settings.AuthPerMinute, TimeSpan.FromMinutes(1));
    }

    /// <summary>One more in the window; whether it is allowed, and how long until the window ends.</summary>
    private async Task<(bool Allowed, int SecondsLeft)> CountAsync(HttpContext context, string key, int limit, TimeSpan window)
    {
        var redis = context.RequestServices.GetService<IConnectionMultiplexer>();

        if (redis is not null)
        {
            try
            {
                var database = redis.GetDatabase();
                var redisKey = $"neoprint:ratelimit:{key}";
                var count = await database.StringIncrementAsync(redisKey).ConfigureAwait(false);

                if (count == 1)
                {
                    await database.KeyExpireAsync(redisKey, window).ConfigureAwait(false);
                }

                var left = await database.KeyTimeToLiveAsync(redisKey).ConfigureAwait(false) ?? window;

                return (count <= limit, Math.Max(1, (int)Math.Ceiling(left.TotalSeconds)));
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                _logger.LogWarning(exception, "Redis did not answer; the device limit {Policy} is counted in memory on this instance.", key.Split(':')[0]);
            }
        }

        var now = DateTime.UtcNow;
        var entry = Fallback.AddOrUpdate(
            key,
            _ => (now + window, 1),
            (_, current) => current.WindowEnds <= now ? (now + window, 1) : (current.WindowEnds, current.Count + 1));

        return (entry.Count <= limit, Math.Max(1, (int)Math.Ceiling((entry.WindowEnds - now).TotalSeconds)));
    }

    /// <summary>A property by name, in any case - as model binding reads it.</summary>
    private static JsonElement? Property(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    /// <summary>The body as JSON, read so the action can read it again; null when it is not JSON or too large.</summary>
    private static async Task<JsonElement?> ReadAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > MaxBody)
        {
            return null;
        }

        context.Request.EnableBuffering(MaxBody);

        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }
}
