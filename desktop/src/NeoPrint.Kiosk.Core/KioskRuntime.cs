using Microsoft.Extensions.Logging;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// What an activated kiosk does continuously: hold its configuration, report
/// in, and work out what it may still do when the backend goes quiet
/// (plan sections 7.3, 7.9, 7.11).
/// </summary>
/// <remarks>
/// The configuration is cached in memory with the time it was fetched, which is
/// what <see cref="KioskAvailabilityRules"/> needs to tell a kiosk that may
/// keep printing paid work from one that has to stop. It is not written to disk
/// here: what is on disk is the device identity, and the configuration is
/// cheap to fetch again (plan section 7.11).
/// </remarks>
public sealed class KioskRuntime
{
    private readonly KioskApiClient _api;
    private readonly IClock _clock;
    private readonly ILogger<KioskRuntime> _logger;
    private readonly ConfigurationCache? _cache;

    private KioskConfiguration? _configuration;
    private DateTime? _configurationFetchedAt;
    private DateTime? _configurationChangedAt;

    public KioskRuntime(KioskApiClient api, IClock clock, ILogger<KioskRuntime> logger, ConfigurationCache? cache = null)
    {
        _cache = cache;
        _api = api;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The configuration in hand, or null before the first fetch.</summary>
    public KioskConfiguration? Configuration => _configuration;

    /// <summary>Whether the last exchange with the backend succeeded.</summary>
    public bool IsBackendReachable { get; private set; }

    /// <summary>How long the last heartbeat took to be answered; null until one has been (plan section 7.9).</summary>
    public int? LastBackendLatencyMs { get; private set; }

    /// <summary>When the configuration in use was fetched from the backend.</summary>
    public DateTime? ConfigurationFetchedAt => _configurationFetchedAt;

    /// <summary>How long to wait before the next telemetry report (<c>Kiosk.TelemetrySeconds</c>).</summary>
    public TimeSpan TelemetryInterval => TimeSpan.FromSeconds(Math.Max(5, _configuration?.TelemetrySeconds ?? 30));

    /// <summary>This build's version, as the heartbeat and telemetry report it.</summary>
    public static string AppVersion => ThisAppVersion;

    /// <summary>A new configuration was loaded: the printer, limits or prices may have changed.</summary>
    public event Action<KioskConfiguration>? ConfigurationChanged;

    /// <summary>
    /// Starts from the configuration cached on disk, when the backend cannot
    /// be reached at start (plan section 7.7). Its age counts from when it was
    /// fetched, so a stale cache still means Offline rather than Degraded.
    /// </summary>
    /// <returns>Whether there was one.</returns>
    public bool LoadCachedConfiguration()
    {
        if (_cache?.Load() is not { } cached)
        {
            return false;
        }

        _configuration = cached.Configuration;
        _configurationFetchedAt = cached.FetchedAt;
        IsBackendReachable = false;

        _logger.LogInformation("Started from the cached configuration of {FetchedAt:u}.", cached.FetchedAt);
        ConfigurationChanged?.Invoke(cached.Configuration);

        return true;
    }

    /// <summary>Reloads now: the hub said the printer or the configuration changed.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken) => LoadConfigurationAsync(cancellationToken);

    /// <summary>What the kiosk may do right now.</summary>
    public KioskAvailability Availability => KioskAvailabilityRules.Decide(
        IsBackendReachable,
        _configurationFetchedAt,
        TimeSpan.FromHours(_configuration?.CacheTtlHours ?? 0),
        _clock.UtcNow);

    /// <summary>How long to wait before the next heartbeat.</summary>
    public TimeSpan HeartbeatInterval =>
        TimeSpan.FromSeconds(Math.Max(5, _configuration?.HeartbeatSeconds ?? 15));

    /// <summary>
    /// Fetches the configuration and keeps it, with the time it arrived.
    /// </summary>
    public async Task<KioskConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await _api.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        _configuration = configuration;
        _configurationFetchedAt = _clock.UtcNow;
        _configurationChangedAt = null;
        IsBackendReachable = true;

        _cache?.Save(configuration, _configurationFetchedAt.Value);

        ConfigurationChanged?.Invoke(configuration);

        _logger.LogInformation(
            "Configuration loaded for kiosk {KioskCode}: printer {Printer}, cache valid {Hours} h.",
            configuration.Code,
            configuration.SelectedPrinter ?? "(none selected)",
            configuration.CacheTtlHours);

        return configuration;
    }

    /// <summary>
    /// Sends one heartbeat and acts on the answer.
    /// </summary>
    /// <remarks>
    /// Refetches the configuration when the backend says it has changed, which
    /// is how a printer chosen in the panel reaches the kiosk without it
    /// polling the whole document.
    /// </remarks>
    /// <returns>
    /// <c>false</c> when the backend could not be reached, so the caller can
    /// show the right screen rather than treating it as a crash.
    /// </returns>
    /// <summary>
    /// The UI started in safe mode after a crash loop (plan section 7.12): the
    /// heartbeat says so, and the backend takes the kiosk out of service.
    /// </summary>
    public bool SafeMode { get; set; }

    /// <param name="printerProblem">
    /// What is wrong when the printer is not ready, as a message key
    /// (<c>printer.outOfPaper</c>): the backend pauses the kiosk and opens the
    /// incident under that name (plan section 9.6).
    /// </param>
    public async Task<bool> BeatAsync(bool printerOnline, CancellationToken cancellationToken = default, string? printerProblem = null)
    {
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();

            var response = await _api
                .SendHeartbeatAsync(
                    _configuration is null ? null : ThisAppVersion,
                    _configuration is null ? null : ThisAppVersion,
                    printerOnline,
                    cancellationToken,
                    SafeMode,
                    printerOnline ? null : printerProblem)
                .ConfigureAwait(false);

            IsBackendReachable = true;
            LastBackendLatencyMs = (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds);

            if (_configurationChangedAt is null)
            {
                _configurationChangedAt = response.ConfigurationChangedAt;
            }
            else if (response.ConfigurationChangedAt > _configurationChangedAt)
            {
                _logger.LogInformation("The kiosk configuration changed; fetching it again.");

                await LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
                _configurationChangedAt = response.ConfigurationChangedAt;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The kiosk is stopping. A cancellation nobody asked for - a
            // timeout somewhere below - is a beat that did not get through,
            // and is handled as one further down.
            throw;
        }
        catch (DeviceRevokedException)
        {
            // Detached: the caller sends the kiosk back to its activation
            // screen rather than retrying (plan section 7.11).
            IsBackendReachable = true;
            throw;
        }
        catch (Exception exception)
        {
            // A kiosk loses its link regularly and that is not a fault; the
            // screen says so and the next beat tries again
            // (docs/FAILURE_RECOVERY.md).
            IsBackendReachable = false;

            _logger.LogWarning(exception, "A heartbeat did not reach the backend.");

            return false;
        }
    }

    /// <summary>The version this build reports.</summary>
    private static string ThisAppVersion =>
        typeof(KioskRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
