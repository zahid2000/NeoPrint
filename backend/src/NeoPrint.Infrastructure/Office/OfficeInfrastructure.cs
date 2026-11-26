using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Office;

using StackExchange.Redis;

namespace NeoPrint.Infrastructure.Office;

/// <inheritdoc cref="IOfficeSessionStore" />
/// <remarks>
/// <c>office:{sha256(token)}</c>, expiring with the session. Lost with Redis,
/// the editor asks to be opened again; nothing is lost but unsaved typing.
/// </remarks>
public sealed class RedisOfficeSessionStore : IOfficeSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly IClock _clock;

    public RedisOfficeSessionStore(IConnectionMultiplexer redis, IClock clock)
    {
        _redis = redis;
        _clock = clock;
    }

    public async Task<string> CreateAsync(OfficeSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var ttl = session.ExpiresAt - _clock.UtcNow;
        await _redis.GetDatabase()
            .StringSetAsync(Key(token), JsonSerializer.Serialize(session, Json), ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1))
            .ConfigureAwait(false);

        return token;
    }

    public async Task<OfficeSession?> FindAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }

        var value = await _redis.GetDatabase().StringGetAsync(Key(token)).ConfigureAwait(false);
        var session = value.HasValue ? JsonSerializer.Deserialize<OfficeSession>(value.ToString(), Json) : null;

        return session is not null && session.ExpiresAt > _clock.UtcNow ? session : null;
    }

    private static string Key(string token)
        => "office:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <inheritdoc cref="IOfficeDiscovery" />
/// <remarks>
/// Collabora's <c>/hosting/discovery</c>, read inside the stack and kept for a
/// few minutes. The page's address is returned as a path: the browser loads
/// it from the API's own origin, where the panel's nginx passes
/// <c>/browser</c> and <c>/cool</c> on to Collabora - so its host name and
/// scheme never have to be configured twice.
/// </remarks>
public sealed class CollaboraDiscovery : IOfficeDiscovery
{
    public const string HttpClientName = "collabora";

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>A Collabora that did not answer is asked again this soon, not on every tap.</summary>
    private static readonly TimeSpan RetryDown = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _http;
    private readonly OfficeOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<CollaboraDiscovery> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, string>? _editors;
    private DateTime _readAt = DateTime.MinValue;

    public CollaboraDiscovery(IHttpClientFactory http, OfficeOptions options, IClock clock, ILogger<CollaboraDiscovery> logger)
    {
        _http = http;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<string?> EditorPathAsync(string extension, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CollaboraUrl))
        {
            return null;
        }

        var editors = await EditorsAsync(cancellationToken).ConfigureAwait(false);
        return editors is not null && editors.TryGetValue(extension.ToLowerInvariant(), out var path) ? path : null;
    }

    private async Task<Dictionary<string, string>?> EditorsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        if (now - _readAt < (_editors is null ? RetryDown : Fresh))
        {
            return _editors;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (now - _readAt < (_editors is null ? RetryDown : Fresh))
            {
                return _editors;
            }

            try
            {
                using var client = _http.CreateClient(HttpClientName);
                var xml = await client
                    .GetStringAsync(new Uri(new Uri(_options.CollaboraUrl), "/hosting/discovery"), cancellationToken)
                    .ConfigureAwait(false);

                _editors = Parse(xml);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                // Not running (switched off on this server) or still starting.
                _logger.LogWarning("Collabora's discovery could not be read: {Reason}", exception.Message);
                _editors = null;
            }

            _readAt = now;
            return _editors;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Extension → the edit page's path and query (<c>/browser/{hash}/cool.html?</c>).</summary>
    public static Dictionary<string, string> Parse(string discovery)
    {
        var editors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var action in XDocument.Parse(discovery).Descendants("action"))
        {
            var extension = (string?)action.Attribute("ext");
            var source = (string?)action.Attribute("urlsrc");

            if ((string?)action.Attribute("name") != "edit"
                || string.IsNullOrEmpty(extension)
                || !Uri.TryCreate(source, UriKind.Absolute, out var url))
            {
                continue;
            }

            editors.TryAdd(extension, url.PathAndQuery);
        }

        return editors;
    }
}
