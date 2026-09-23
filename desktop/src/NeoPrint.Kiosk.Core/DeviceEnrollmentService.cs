using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// What state this installation is in (plan section 7.11).
/// </summary>
public enum KioskLifecycle
{
    /// <summary>No key yet, or not registered with the backend.</summary>
    Unregistered = 0,

    /// <summary>Registered and showing a pairing code, waiting for an admin.</summary>
    Pending = 1,

    /// <summary>Activated and bound to a kiosk.</summary>
    Active = 2,

    /// <summary>Detached by an admin; the local identity is discarded.</summary>
    Revoked = 3,
}

/// <summary>What the activation screen shows.</summary>
/// <param name="Lifecycle">Where the device is in its lifecycle.</param>
/// <param name="PairingCode">The code to display, when waiting.</param>
/// <param name="PairingCodeExpiresAt">When the code stops working.</param>
/// <param name="ActivationQr">Content for the activation QR an operator scans.</param>
/// <param name="DeviceId">This installation's identifier.</param>
/// <param name="KioskCode">The kiosk code, once activated.</param>
/// <param name="Printers">Printers reported, shown to the activating admin.</param>
public sealed record EnrollmentState(
    KioskLifecycle Lifecycle,
    string? PairingCode,
    DateTime? PairingCodeExpiresAt,
    string? ActivationQr,
    Guid? DeviceId,
    string? KioskCode,
    IReadOnlyCollection<DetectedPrinter> Printers);

/// <summary>
/// Drives this installation from "nothing configured" to "activated kiosk"
/// (plan sections 7.11, 9.0).
/// </summary>
/// <remarks>
/// The whole point of the universal build lives here: the kiosk starts with no
/// identity at all, generates its own key, registers, shows a code, and picks
/// up its configuration once an administrator activates it. The same binary
/// runs on every kiosk.
/// </remarks>
public sealed class DeviceEnrollmentService
{
    /// <summary>
    /// Where the provisioning script leaves an enrollment token for zero-touch
    /// activation (plan section 7.11).
    /// </summary>
    public const string EnrollmentTokenFileName = "enrollment-token";

    private readonly KioskApiClient _api;
    private readonly IDeviceKeyStore _keys;
    private readonly IAppPaths _paths;
    private readonly IMachineFingerprintProvider _fingerprint;
    private readonly IPrinterService _printers;
    private readonly KioskSettings _settings;
    private readonly ILogger<DeviceEnrollmentService> _logger;

    public DeviceEnrollmentService(
        KioskApiClient api,
        IDeviceKeyStore keys,
        IAppPaths paths,
        IMachineFingerprintProvider fingerprint,
        IPrinterService printers,
        IOptions<KioskSettings> settings,
        ILogger<DeviceEnrollmentService> logger)
    {
        _api = api;
        _keys = keys;
        _paths = paths;
        _fingerprint = fingerprint;
        _printers = printers;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>The platform name reported to the backend.</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";

    /// <summary>This build's version, reported to the backend.</summary>
    public static string AppVersion =>
        typeof(DeviceEnrollmentService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Registers if needed, authenticates, and reports where the device stands.
    /// </summary>
    /// <remarks>
    /// Called on every start and then periodically while waiting. Registering
    /// again is harmless: the backend recognises the key and keeps the same
    /// device, which is why a restart or an update never needs re-activating.
    /// </remarks>
    public async Task<EnrollmentState> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        var printers = await DiscoverPrintersAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var registration = await _api.RegisterAsync(
                    _fingerprint.Collect(),
                    CurrentPlatform,
                    AppVersion,
                    ReadEnrollmentToken(),
                    printers,
                    cancellationToken)
                .ConfigureAwait(false);

            var token = await _api.AuthenticateAsync(cancellationToken).ConfigureAwait(false);

            if (_api.IsActivated)
            {
                // The token that activated this device is spent; remove it so a
                // later reinstall cannot reuse it.
                ConsumeEnrollmentToken();

                _logger.LogInformation(
                    "This device is activated as kiosk {KioskCode}.", _api.KioskCode);

                return new EnrollmentState(
                    KioskLifecycle.Active, null, null, null, _api.DeviceId, _api.KioskCode, printers);
            }

            // Still waiting: report printers so the administrator can pick one
            // while activating (plan section 9.0).
            await SafeReportPrintersAsync(printers, cancellationToken).ConfigureAwait(false);

            var code = registration.PairingCode;
            var expiresAt = registration.PairingCodeExpiresAt;

            if (code is null || expiresAt <= DateTime.UtcNow)
            {
                var renewed = await _api.RenewPairingCodeAsync(cancellationToken).ConfigureAwait(false);
                code = renewed.PairingCode;
                expiresAt = renewed.ExpiresAt;
            }

            return new EnrollmentState(
                KioskLifecycle.Pending,
                code,
                expiresAt,
                BuildActivationQr(_api.DeviceId!.Value, code),
                _api.DeviceId,
                null,
                printers);
        }
        catch (DeviceRevokedException exception)
        {
            _logger.LogWarning(exception, "The backend refused this device; discarding the local identity.");

            HandleRevocation();

            return new EnrollmentState(
                KioskLifecycle.Revoked, null, null, null, null, null, printers);
        }
    }

    /// <summary>
    /// Discards the local identity after a revocation, so the next start
    /// registers as a new device (plan section 7.11).
    /// </summary>
    public void HandleRevocation()
    {
        _api.ClearToken();
        _keys.Delete();
        ClearCachedConfiguration();
    }

    /// <summary>
    /// The content of the QR an operator scans from the kiosk screen
    /// (plan section 2.9).
    /// </summary>
    public static string BuildActivationQr(Guid deviceId, string? pairingCode)
        => pairingCode is null
            ? $"neoprint://activate/{deviceId}"
            : $"neoprint://activate/{deviceId}?code={Uri.EscapeDataString(pairingCode)}";

    /// <summary>
    /// Reads the enrollment token the provisioning script left behind, if any.
    /// </summary>
    private string? ReadEnrollmentToken()
    {
        var path = Path.Combine(_paths.DeviceDirectory, EnrollmentTokenFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "An enrollment token file is present but could not be read.");
            return null;
        }
    }

    private void ConsumeEnrollmentToken()
    {
        var path = Path.Combine(_paths.DeviceDirectory, EnrollmentTokenFileName);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The spent enrollment token file could not be deleted.");
        }
    }

    private void ClearCachedConfiguration()
    {
        foreach (var directory in new[] { _paths.DeviceDirectory, _paths.SpoolDirectory, _paths.StateDirectory })
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Clearing {Directory} after revocation failed.", directory);
            }
        }
    }

    private async Task<IReadOnlyList<DetectedPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _printers.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A printer subsystem that will not answer must not stop the kiosk
            // registering; the administrator can refresh the list later.
            _logger.LogWarning(exception, "Discovering printers failed.");
            return [];
        }
    }

    private async Task SafeReportPrintersAsync(
        IReadOnlyCollection<DetectedPrinter> printers, CancellationToken cancellationToken)
    {
        if (printers.Count == 0)
        {
            return;
        }

        try
        {
            await _api.ReportPrintersAsync(printers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not DeviceRevokedException)
        {
            _logger.LogWarning(exception, "Reporting printers failed.");
        }
    }
}
