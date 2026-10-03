using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Notifications;
using NeoPrint.Domain.Users;

namespace NeoPrint.Infrastructure.Notifications;

/// <summary>
/// The parts of a Firebase service account key the push needs
/// (<c>NEOPRINT_FCM_SERVICE_ACCOUNT_JSON</c> in OpenBao, plan section 2.13).
/// </summary>
public sealed class FcmServiceAccount
{
    public const string ConfigurationKey = "NEOPRINT_FCM_SERVICE_ACCOUNT_JSON";

    private FcmServiceAccount(string projectId, string clientEmail, string? keyId, string privateKeyPem, Uri tokenUri)
    {
        ProjectId = projectId;
        ClientEmail = clientEmail;
        KeyId = keyId;
        PrivateKeyPem = privateKeyPem;
        TokenUri = tokenUri;
    }

    public string ProjectId { get; }

    public string ClientEmail { get; }

    public string? KeyId { get; }

    /// <summary>The private key. Never logged, never put in an exception message.</summary>
    public string PrivateKeyPem { get; }

    public Uri TokenUri { get; }

    /// <summary>
    /// Reads the key. <paramref name="problem"/> says what is wrong without
    /// quoting any of it, so it can be logged.
    /// </summary>
    public static FcmServiceAccount? TryParse(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            if (Text("type") != "service_account")
            {
                problem = "not a service account key (type)";
                return null;
            }

            var projectId = Text("project_id");
            var clientEmail = Text("client_email");
            var privateKey = Text("private_key");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
            {
                problem = "project_id, client_email or private_key missing";
                return null;
            }

            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(privateKey);
            }

            var tokenUri = Uri.TryCreate(Text("token_uri"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : new Uri("https://oauth2.googleapis.com/token");

            return new FcmServiceAccount(projectId, clientEmail, Text("private_key_id"), privateKey, tokenUri);
        }
        catch (JsonException)
        {
            problem = "not JSON";
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            problem = "private_key is not an RSA key in PEM";
            return null;
        }
    }
}

/// <summary>
/// Push through the FCM HTTP v1 API with our own client (plan section 2.0:
/// <c>HttpClient</c> + a service account JWT signed with the BCL's RSA, no
/// FirebaseAdmin).
/// </summary>
/// <remarks>
/// <para>
/// An OAuth2 access token is asked for with a self-signed JWT (RFC 7523) and
/// kept until five minutes before it runs out; a 401 drops it and asks once
/// more.
/// </para>
/// <para>
/// The registration token is a credential for the device and the message may
/// carry a file name: neither is logged - only the token's hash, the HTTP
/// status and FCM's error code.
/// </para>
/// </remarks>
public sealed class FcmPushSender : IPushSender, IDisposable
{
    public const string HttpClientName = "fcm";

    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly FcmServiceAccount _account;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<FcmPushSender> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenUntil;

    public FcmPushSender(FcmServiceAccount account, IHttpClientFactory http, TimeProvider time, ILogger<FcmPushSender> logger)
    {
        _account = account;
        _http = http;
        _time = time;
        _logger = logger;
    }

    public async Task<PushResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(message);

        var hash = PushDevice.Hash(token);

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var accessToken = await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
                if (accessToken is null)
                {
                    return PushResult.Failed;
                }

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(_account.ProjectId)}/messages:send")
                {
                    Content = JsonContent.Create(Body(token, message), options: Json),
                };
                request.Headers.Authorization = new("Bearer", accessToken);

                using var response = await _http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return PushResult.Delivered;
                }

                var error = await ErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    ForgetAccessToken();
                    continue;
                }

                // UNREGISTERED: the app was removed or the token rotated; a token
                // from another Firebase project, or one that is not a token at all.
                if (error is "UNREGISTERED" or "SENDER_ID_MISMATCH"
                    || (response.StatusCode == HttpStatusCode.BadRequest && error is "INVALID_ARGUMENT"))
                {
                    _logger.LogInformation(
                        "FCM refused token {TokenHash}: {Status} {Error}.", hash, (int)response.StatusCode, error);
                    return PushResult.TokenGone;
                }

                _logger.LogWarning(
                    "FCM did not take the push for token {TokenHash}: {Status} {Error}; it is retried.",
                    hash, (int)response.StatusCode, error);
                return PushResult.Failed;
            }

            return PushResult.Failed;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("FCM could not be reached for token {TokenHash} ({Error}); it is retried.", hash, exception.GetType().Name);
            return PushResult.Failed;
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    /// <summary>
    /// The FCM v1 message: a notification the phone shows by itself when the
    /// app is closed, plus the data the app needs to open the right screen.
    /// </summary>
    private static object Body(string token, PushMessage message) => new
    {
        message = new
        {
            token,
            notification = new { title = message.Title, body = message.Body },
            data = message.Data,
            android = new { priority = "HIGH" },
            apns = new { payload = new { aps = new { sound = "default" } } },
        },
    };

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var error = document.RootElement.GetProperty("error");

            // google.firebase.fcm.v1.FcmError's errorCode is the precise one; status is the gRPC code.
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.TryGetProperty("errorCode", out var code) && code.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }

            return error.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private void ForgetAccessToken() => _accessToken = null;

    private async Task<string?> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && _time.GetUtcNow() < _accessTokenUntil)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } fresh && _time.GetUtcNow() < _accessTokenUntil)
            {
                return fresh;
            }

            var now = _time.GetUtcNow();
            using var response = await _http.CreateClient(HttpClientName)
                .PostAsync(
                    _account.TokenUri,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                        ["assertion"] = Assertion(now),
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // invalid_grant here usually means the key was deleted in the console.
                _logger.LogError(
                    "Google refused the FCM access token for {ClientEmail}: {Status}. Is the service account key still valid?",
                    _account.ClientEmail, (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var token = document.RootElement.GetProperty("access_token").GetString();
            var lifetime = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;

            _accessToken = token;
            _accessTokenUntil = now.AddSeconds(Math.Max(60, lifetime - 300));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>The self-signed JWT: RS256 over header.claims, valid for an hour.</summary>
    private string Assertion(DateTimeOffset now)
    {
        var header = new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (_account.KeyId is { Length: > 0 } keyId)
        {
            header["kid"] = keyId;
        }

        var claims = new Dictionary<string, object>
        {
            ["iss"] = _account.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = _account.TokenUri.ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        };

        var signingInput = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_account.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Stands in for FCM when no key is configured (plan section 2.1): nothing is
/// sent, the log says what would have been. The list and the hub still carry
/// every notification.
/// </summary>
public sealed class LoggingPushSender : IPushSender
{
    private readonly ILogger<LoggingPushSender> _logger;

    public LoggingPushSender(ILogger<LoggingPushSender> logger) => _logger = logger;

    public Task<PushResult> SendAsync(string token, PushMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(message);

        _logger.LogInformation(
            "Push not sent (FCM not configured): notification {NotificationId} ({Type}) to device {TokenHash}.",
            message.Data.GetValueOrDefault("notificationId"),
            message.Data.GetValueOrDefault("type"),
            PushDevice.Hash(token));

        return Task.FromResult(PushResult.Delivered);
    }
}
