using CommunityToolkit.Mvvm.ComponentModel;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The activation screen, screen 0 (plan section 7.3).
/// </summary>
/// <remarks>
/// What a brand-new kiosk shows: a large pairing code, the QR an operator can
/// scan, the countdown to the code refreshing, what machine this is, and
/// whether the backend is reachable. Until an administrator activates it the
/// kiosk accepts nothing and has no configuration to accept it with
/// (plan section 7.11).
/// </remarks>
public sealed partial class ActivationViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _pairingCode;

    [ObservableProperty]
    private DateTime? _pairingCodeExpiresAt;

    [ObservableProperty]
    private string? _activationQr;

    [ObservableProperty]
    private bool _isBackendReachable;

    [ObservableProperty]
    private KioskLifecycle _lifecycle = KioskLifecycle.Unregistered;

    [ObservableProperty]
    private MachineFingerprint? _fingerprint;

    [ObservableProperty]
    private IReadOnlyCollection<DetectedPrinter> _printers = [];

    /// <summary>How long before the code refreshes; recomputed by the UI timer.</summary>
    [ObservableProperty]
    private TimeSpan _timeUntilRefresh;

    public string Title => T("activation.title");

    public string Instruction => T("activation.instruction");

    public string DeviceInfoHeading => T("activation.deviceInfo");

    public string HostnameLabel => T("activation.hostname");

    public string PlatformLabel => T("activation.platform");

    public string VersionLabel => T("activation.version");

    public string PrintersHeading => T("activation.printers");

    /// <summary>The printer list, or a note that none was found.</summary>
    public string PrintersSummary => Printers.Count == 0
        ? T("activation.noPrinters")
        : string.Join(", ", Printers.Select(printer => printer.Name));

    public string ConnectionStatus => IsBackendReachable
        ? T("activation.connected")
        : T("activation.disconnected");

    /// <summary>
    /// The countdown, or the revocation notice while a new identity is being
    /// created.
    /// </summary>
    public string CodeExpiryText => Lifecycle == KioskLifecycle.Revoked
        ? T("activation.revoked")
        : T("activation.codeExpires", FormatCountdown(TimeUntilRefresh));

    public string Hostname => Fingerprint?.Hostname ?? "—";

    public string Platform => DeviceEnrollmentService.CurrentPlatform;

    public string Version => DeviceEnrollmentService.AppVersion;

    /// <summary>
    /// The code split for display, so a four-plus-four group is easy to read
    /// out over the phone (plan section 7.11).
    /// </summary>
    public string DisplayCode => PairingCode ?? "····-····";

    /// <summary>Applies the latest enrollment state.</summary>
    public void Apply(EnrollmentState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Lifecycle = state.Lifecycle;
        PairingCode = state.PairingCode;
        PairingCodeExpiresAt = state.PairingCodeExpiresAt;
        ActivationQr = state.ActivationQr;
        Printers = state.Printers;

        Tick();
    }

    /// <summary>Recomputes the countdown; called once a second by the UI.</summary>
    public void Tick()
    {
        var remaining = PairingCodeExpiresAt is null
            ? TimeSpan.Zero
            : PairingCodeExpiresAt.Value - DateTime.UtcNow;

        TimeUntilRefresh = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Renders a countdown as <c>m:ss</c>.</summary>
    public static string FormatCountdown(TimeSpan remaining)
        => remaining <= TimeSpan.Zero
            ? "0:00"
            : $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2}";

    // These are computed from other properties, so they have to be re-raised
    // when their inputs change; the generated setters do not know about them.
    partial void OnPrintersChanged(IReadOnlyCollection<DetectedPrinter> value)
        => OnPropertyChanged(nameof(PrintersSummary));

    partial void OnIsBackendReachableChanged(bool value)
        => OnPropertyChanged(nameof(ConnectionStatus));

    partial void OnPairingCodeChanged(string? value)
        => OnPropertyChanged(nameof(DisplayCode));

    partial void OnTimeUntilRefreshChanged(TimeSpan value)
        => OnPropertyChanged(nameof(CodeExpiryText));

    partial void OnLifecycleChanged(KioskLifecycle value)
        => OnPropertyChanged(nameof(CodeExpiryText));

    partial void OnFingerprintChanged(MachineFingerprint? value)
        => OnPropertyChanged(nameof(Hostname));
}
