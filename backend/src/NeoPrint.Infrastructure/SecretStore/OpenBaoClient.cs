using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoPrint.Infrastructure.SecretStore;

/// <summary>
/// Reads secrets from OpenBao.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Reads every key at one KV v2 path.
    /// </summary>
    /// <param name="path">Path relative to the mount, for example <c>backend/core</c>.</param>
    Task<IReadOnlyDictionary<string, string?>> ReadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Our own lightweight OpenBao client: AppRole login, KV v2 reads and token
/// renewal over <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Written by hand rather than taking a Vault client package, as plan section
/// 2.13 requires. It uses only the standard HTTP API, so it works against
/// OpenBao and against Vault-compatible servers alike.
/// </remarks>
public sealed class OpenBaoClient : ISecretStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly OpenBaoOptions _options;
    private readonly Action<string, Exception?> _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpiresAt;
    private bool _tokenRenewable;

    public OpenBaoClient(
        HttpClient http,
        OpenBaoOptions options,
        Action<string, Exception?>? log = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        _http = http;
        _options = options;
        _log = log ?? ((_, _) => { });
        _time = timeProvider ?? TimeProvider.System;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(options.Address.TrimEnd('/') + "/");
        }
    }

    /// <summary>Renew when less than this much of the lease is left.</summary>
    private static readonly TimeSpan RenewWindow = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendReadAsync(path, token, cancellationToken).ConfigureAwait(false);

        // A token can be revoked or expire between two reads; log in again once
        // before treating it as a failure.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _log($"OpenBao rejected the token while reading '{path}'; logging in again.", null);

            InvalidateToken();
            token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
            response = await SendReadAsync(path, token, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A path that has never been written is not an error here. It
                // becomes one only if a mandatory key turns out to be missing.
                return new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SecretStoreUnavailableException(
                    $"OpenBao returned {(int)response.StatusCode} while reading '{_options.KvMount}/{path}'.");
            }

            KvReadResponse? payload;
            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<KvReadResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new SecretStoreUnavailableException(
                    $"OpenBao returned a response for '{_options.KvMount}/{path}' that could not be parsed.", exception);
            }

            var values = payload?.Data?.Data;
            if (values is null)
            {
                return new Dictionary<string, string?>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, string?>(values.Count, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                result[key] = ToStringValue(value);
            }

            return result;
        }
    }

    private Task<HttpResponseMessage> SendReadAsync(string path, string token, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/{Uri.EscapeDataString(_options.KvMount)}/data/{EscapePath(path)}");
        request.Headers.Add("X-Vault-Token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_token is not null && now < _tokenExpiresAt - RenewWindow)
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_token is not null && now < _tokenExpiresAt - RenewWindow)
            {
                return _token;
            }

            // Prefer renewing the lease we already hold; fall back to a fresh
            // login when it is not renewable or renewal fails.
            if (_token is not null && _tokenRenewable && now < _tokenExpiresAt)
            {
                if (await TryRenewAsync(_token, cancellationToken).ConfigureAwait(false))
                {
                    return _token;
                }

                _log("Renewing the OpenBao token failed; logging in again.", null);
            }

            await LoginAsync(cancellationToken).ConfigureAwait(false);
            return _token!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        var (roleId, secretId) = _options.ResolveCredentials();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/approle/login")
        {
            Content = JsonContent.Create(new { role_id = roleId, secret_id = secretId }),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new SecretStoreUnavailableException(
                $"Could not reach OpenBao at {_options.Address}: {exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not echo the response body: it can repeat
                // the credentials back.
                throw new SecretStoreUnavailableException(
                    $"OpenBao rejected the AppRole login with {(int)response.StatusCode}. " +
                    "Check the role id and secret id, and that the secret id has not expired or run out of uses.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var auth = payload?.Auth
                ?? throw new SecretStoreUnavailableException("The OpenBao login response contained no auth section.");

            if (string.IsNullOrWhiteSpace(auth.ClientToken))
            {
                throw new SecretStoreUnavailableException("The OpenBao login response contained no client token.");
            }

            _token = auth.ClientToken;
            _tokenRenewable = auth.Renewable;
            _tokenExpiresAt = _time.GetUtcNow().AddSeconds(auth.LeaseDuration > 0 ? auth.LeaseDuration : 3600);
        }
    }

    private async Task<bool> TryRenewAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/token/renew-self")
            {
                Content = JsonContent.Create(new { increment = "1h" }),
            };
            request.Headers.Add("X-Vault-Token", token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var auth = payload?.Auth;
            if (auth is null || auth.LeaseDuration <= 0)
            {
                return false;
            }

            _tokenRenewable = auth.Renewable;
            _tokenExpiresAt = _time.GetUtcNow().AddSeconds(auth.LeaseDuration);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private void InvalidateToken()
    {
        _token = null;
        _tokenExpiresAt = default;
        _tokenRenewable = false;
    }

    /// <summary>Escapes each path segment while keeping the separators.</summary>
    private static string EscapePath(string path)
        => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>
    /// KV v2 stores JSON values. Everything the application consumes is a
    /// configuration string, so scalars are flattened and anything structured
    /// is kept as its JSON text - which is what NEOPRINT_FCM_SERVICE_ACCOUNT_JSON
    /// needs.
    /// </summary>
    private static string? ToStringValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    public void Dispose() => _tokenLock.Dispose();

    private sealed class AuthResponse
    {
        [JsonPropertyName("auth")]
        public AuthSection? Auth { get; set; }
    }

    private sealed class AuthSection
    {
        [JsonPropertyName("client_token")]
        public string? ClientToken { get; set; }

        [JsonPropertyName("lease_duration")]
        public int LeaseDuration { get; set; }

        [JsonPropertyName("renewable")]
        public bool Renewable { get; set; }
    }

    private sealed class KvReadResponse
    {
        [JsonPropertyName("data")]
        public KvDataSection? Data { get; set; }
    }

    private sealed class KvDataSection
    {
        [JsonPropertyName("data")]
        public Dictionary<string, JsonElement>? Data { get; set; }
    }
}
