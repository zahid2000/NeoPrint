using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// Wire contracts
// ---------------------------------------------------------------------------

/// <param name="DeviceId">Identifier the backend assigned.</param>
/// <param name="Activated">True when an enrollment token activated it at once.</param>
/// <param name="PairingCode">The code to show, when there is one.</param>
/// <param name="PairingCodeExpiresAt">When that code stops working.</param>
/// <param name="KioskId">Set once activated.</param>
/// <param name="KioskCode">Set once activated.</param>
public sealed record DeviceRegistrationResponse(
    Guid DeviceId,
    bool Activated,
    string? PairingCode,
    DateTime? PairingCodeExpiresAt,
    Guid? KioskId,
    string? KioskCode);

public sealed record DeviceChallengeResponse(string Nonce, DateTime ExpiresAt);

/// <param name="TokenType"><c>kiosk</c> once activated, <c>provisioning</c> before that.</param>
public sealed record DeviceTokenResponse(
    string AccessToken, DateTime ExpiresAt, string TokenType, Guid? KioskId, string? KioskCode);

public sealed record PairingCodeResponse(string PairingCode, DateTime ExpiresAt);

/// <summary>
/// The kiosk's own configuration, as the backend sends it
/// (plan sections 7.11, 2.2).
/// </summary>
/// <remarks>
/// Cached locally so the kiosk can keep working while the backend is
/// unreachable, for as long as <see cref="CacheTtlHours"/> allows
/// (plan section 7.3). No backend secret is in it - the admin PIN arrives as a
/// PBKDF2 hash the kiosk compares against.
/// </remarks>
public sealed record KioskConfiguration(
    Guid KioskId,
    string Code,
    string Name,
    string Status,
    string DefaultLanguage,
    string? SelectedPrinter,
    PrinterCapabilityFlags EffectiveCapabilities,
    IReadOnlyCollection<ConfiguredPrinter> AvailablePrinters,
    string? AdminPinHash,
    int QueueDisplayLimit,
    int MaxFileSizeMb,
    int MaxPages,
    Guid? PricingProfileId,
    IReadOnlyDictionary<string, PaperStockLevel> PaperStock,
    IReadOnlyDictionary<string, bool> Features,
    int HeartbeatSeconds,
    int CacheTtlHours,
    DateTime ServerTime);

public sealed record PrinterCapabilityFlags(bool Color, bool Duplex, bool A3);

public sealed record ConfiguredPrinter(
    string Name, string? DriverName, bool IsOnline, PrinterCapabilityFlags DetectedCapabilities);

public sealed record PaperStockLevel(int Capacity, int Current);

/// <summary>What the backend says back to a heartbeat (plan section 7.9).</summary>
/// <param name="ConfigurationChangedAt">
/// When the configuration last changed. The kiosk refetches when this moves
/// past what it last saw, rather than polling the whole document.
/// </param>
public sealed record KioskHeartbeatResponse(
    string Status,
    int NextInSeconds,
    DateTime ConfigurationChangedAt,
    DateTime ServerTime);

/// <summary>
/// Raised when the backend refuses the device outright, which in practice means
/// it was revoked (plan section 7.11).
/// </summary>
public sealed class DeviceRevokedException : Exception
{
    public DeviceRevokedException(string message)
        : base(message)
    {
    }
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

/// <summary>
/// The kiosk's HTTP client for the backend (plan sections 6, 7.11).
/// </summary>
/// <remarks>
/// Hand-written over <see cref="HttpClient"/>, as plan section 2.0 requires -
/// no Refit or similar. It holds the kiosk token and renews it from the device
/// key when it expires, so nothing above has to think about tokens.
/// </remarks>
public sealed class KioskApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Renew when less than this is left of the token's life.</summary>
    private static readonly TimeSpan RenewWindow = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IDeviceKeyStore _keys;
    private readonly ILogger<KioskApiClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTime _tokenExpiresAt;

    public KioskApiClient(HttpClient http, IDeviceKeyStore keys, ILogger<KioskApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
        _keys = keys;
        _logger = logger;
    }

    /// <summary>The device this installation is, once registered.</summary>
    public Guid? DeviceId { get; private set; }

    /// <summary>The kiosk this device is bound to, once activated.</summary>
    public Guid? KioskId { get; private set; }

    /// <summary>The kiosk code, shown in the footer.</summary>
    public string? KioskCode { get; private set; }

    /// <summary>Whether the last token the backend issued was a kiosk token.</summary>
    public bool IsActivated { get; private set; }

    /// <summary>
    /// Registers this installation. Safe to call on every start: the backend
    /// recognises the key and keeps the same device.
    /// </summary>
    public async Task<DeviceRegistrationResponse> RegisterAsync(
        MachineFingerprint fingerprint,
        string platform,
        string? appVersion,
        string? enrollmentToken,
        IReadOnlyCollection<DetectedPrinter> printers,
        CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync(
                "api/devices/register",
                new
                {
                    publicKey = _keys.GetOrCreatePublicKey(),
                    fingerprint = new
                    {
                        fingerprint.Hostname,
                        fingerprint.Os,
                        fingerprint.OsVersion,
                        fingerprint.MachineId,
                        fingerprint.MacAddresses,
                        fingerprint.Cpu,
                        fingerprint.RamGb,
                        fingerprint.ScreenResolution,
                    },
                    platform,
                    appVersion,
                    enrollmentToken,
                    printers = printers.Select(printer => new
                    {
                        printer.Name,
                        printer.DriverName,
                        printer.IsOnline,
                        color = printer.SupportsColor,
                        duplex = printer.SupportsDuplex,
                        a3 = printer.SupportsA3,
                    }),
                },
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // The backend refuses a revoked key; reinstalling generates a new one.
            throw new DeviceRevokedException(
                "Bu cihaz ləğv edilib. Yeni cihaz açarı yaradılacaq.");
        }

        response.EnsureSuccessStatusCode();

        var registration = await response.Content
                               .ReadFromJsonAsync<DeviceRegistrationResponse>(JsonOptions, cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new InvalidOperationException("The registration response was empty.");

        DeviceId = registration.DeviceId;
        KioskId = registration.KioskId;
        KioskCode = registration.KioskCode;

        return registration;
    }

    /// <summary>
    /// Obtains a token by signing a challenge with the device key.
    /// </summary>
    public async Task<DeviceTokenResponse> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (DeviceId is null)
        {
            throw new InvalidOperationException("The device must be registered before it can authenticate.");
        }

        var challengeResponse = await _http
            .PostAsJsonAsync("api/devices/challenge", new { deviceId = DeviceId }, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (challengeResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            throw new DeviceRevokedException("Cihaz təsdiqlənmədi. Cihaz ləğv edilmiş ola bilər.");
        }

        challengeResponse.EnsureSuccessStatusCode();

        var challenge = await challengeResponse.Content
                            .ReadFromJsonAsync<DeviceChallengeResponse>(JsonOptions, cancellationToken)
                            .ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The challenge response was empty.");

        var tokenResponse = await _http.PostAsJsonAsync(
                "api/devices/token",
                new
                {
                    deviceId = DeviceId,
                    nonce = challenge.Nonce,
                    signature = _keys.Sign(challenge.Nonce),
                },
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false);

        if (tokenResponse.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Unauthorized)
        {
            throw new DeviceRevokedException("Cihaz təsdiqlənmədi. Cihaz ləğv edilmiş ola bilər.");
        }

        tokenResponse.EnsureSuccessStatusCode();

        var token = await tokenResponse.Content
                        .ReadFromJsonAsync<DeviceTokenResponse>(JsonOptions, cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The token response was empty.");

        _accessToken = token.AccessToken;
        _tokenExpiresAt = token.ExpiresAt;

        IsActivated = string.Equals(token.TokenType, "kiosk", StringComparison.Ordinal);
        KioskId = token.KioskId ?? KioskId;
        KioskCode = token.KioskCode ?? KioskCode;

        _logger.LogInformation(
            "The backend issued a {TokenType} token, valid until {ExpiresAt:u}.", token.TokenType, token.ExpiresAt);

        return token;
    }

    /// <summary>Asks for a fresh pairing code for the activation screen.</summary>
    public async Task<PairingCodeResponse> RenewPairingCodeAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Post, "api/devices/me/pairing-code", cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<PairingCodeResponse>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidOperationException("The pairing code response was empty.");
    }

    /// <summary>Reports the printers this machine can see.</summary>
    public async Task ReportPrintersAsync(
        IReadOnlyCollection<DetectedPrinter> printers, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Put, "api/devices/me/printers", cancellationToken).ConfigureAwait(false);

        request.Content = JsonContent.Create(
            new
            {
                printers = printers.Select(printer => new
                {
                    printer.Name,
                    printer.DriverName,
                    printer.IsOnline,
                    color = printer.SupportsColor,
                    duplex = printer.SupportsDuplex,
                    a3 = printer.SupportsA3,
                }),
            },
            options: JsonOptions);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Fetches this kiosk's own configuration (plan section 7.11).
    /// </summary>
    /// <remarks>
    /// Everything that makes this installation a particular kiosk comes from
    /// here: the build itself is universal and carries none of it.
    /// </remarks>
    public async Task<KioskConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Get, "api/kiosks/me/config", cancellationToken).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<KioskConfiguration>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidOperationException("The configuration response was empty.");
    }

    /// <summary>
    /// Reports that this kiosk is alive (plan section 7.9).
    /// </summary>
    /// <returns>
    /// What the backend says back, including when to send the next one and
    /// whether the configuration has changed since it was fetched.
    /// </returns>
    public async Task<KioskHeartbeatResponse> SendHeartbeatAsync(
        string? appVersion,
        string? agentVersion,
        bool printerOnline,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
            HttpMethod.Post, "api/kiosks/heartbeat", cancellationToken).ConfigureAwait(false);

        request.Content = JsonContent.Create(
            new { appVersion, agentVersion, printerOnline }, options: JsonOptions);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<KioskHeartbeatResponse>(JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidOperationException("The heartbeat response was empty.");
    }

    /// <summary>Forgets the cached token, so the next call signs a new challenge.</summary>
    public void ClearToken()
    {
        _accessToken = null;
        _tokenExpiresAt = default;
        IsActivated = false;
    }

    /// <summary>
    /// Builds a request carrying a valid token, renewing it first if it is
    /// close to expiring.
    /// </summary>
    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method, string path, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTime.UtcNow < _tokenExpiresAt - RenewWindow)
        {
            return;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is null || DateTime.UtcNow >= _tokenExpiresAt - RenewWindow)
            {
                await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
