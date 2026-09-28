using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Crypto;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>Where the technician is in the admin menu.</summary>
public enum AdminStage
{
    /// <summary>Typing the kiosk's admin PIN.</summary>
    Pin = 0,

    /// <summary>The PIN was right: the actions.</summary>
    Menu = 1,

    /// <summary>Too many wrong PINs: nothing is accepted for a while.</summary>
    Locked = 2,

    /// <summary>The kiosk has no PIN yet (not activated): there is no way in from the screen.</summary>
    NoPin = 3,
}

/// <summary>
/// The hidden admin exit (plan sections 7.1, 7.12): five taps on the logo, the
/// kiosk's admin PIN, then what a technician on site may do - leave safe mode,
/// restart the application, or hand the machine over for maintenance.
/// </summary>
/// <remarks>
/// <para>
/// The PIN is checked against the PBKDF2 hash the backend sends in the
/// configuration; the PIN itself never leaves the admin panel. Five wrong ones
/// lock the menu for five minutes, and the digits are never logged.
/// </para>
/// <para>
/// Typed on the screen's own keypad: the physical keyboard is locked down
/// (docs/DESIGN_SYSTEM.md, on-screen keyboard; docs/TASKS.md T-072).
/// </para>
/// </remarks>
public sealed partial class AdminMenuViewModel : ViewModelBase
{
    public const int MaxAttempts = 5;

    public const int MaxPinLength = 12;

    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);

    private readonly Func<string?> _pinHash;
    private readonly Func<DateTime> _now;

    private int _failures;
    private DateTime? _lockedUntil;

    [ObservableProperty]
    private AdminStage _stage;

    [ObservableProperty]
    private string _pinInput = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isSafeMode;

    /// <param name="pinHash">The admin PIN hash from the kiosk's configuration; null before activation.</param>
    public AdminMenuViewModel(Func<string?> pinHash, Func<DateTime>? now = null)
    {
        _pinHash = pinHash;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Leave safe mode and start again (plan section 7.12).</summary>
    public event Action? LeaveSafeModeRequested;

    public event Action? RestartRequested;

    /// <summary>Hand the machine to the technician: sign out (Windows) or the console (Linux).</summary>
    public event Action? MaintenanceRequested;

    public event Action? Closed;

    /// <summary>The PIN as dots: the digits are not shown to whoever stands behind.</summary>
    public string PinDots => new('●', PinInput.Length);

    public string PinTitle => T("admin.pinTitle");

    public string PinBody => T("admin.pinBody");

    public string MenuTitle => T("admin.menuTitle");

    public string LeaveSafeModeText => T("admin.leaveSafeMode");

    public string RestartText => T("admin.restart");

    public string MaintenanceText => T("admin.maintenance");

    public string CloseText => T("common.cancel");

    public string EnterText => T("admin.enter");

    public string EraseText => T("keyboard.backspace");

    public string LockedText => T("admin.locked", (int)LockTime.TotalMinutes);

    public string NoPinText => T("admin.noPin");

    /// <summary>Shown when the gesture is done: PIN first, or the reason there is no way in.</summary>
    public void Open(bool safeMode)
    {
        IsSafeMode = safeMode;
        PinInput = string.Empty;
        Error = null;

        Stage = _lockedUntil is { } until && until > _now() ? AdminStage.Locked
            : string.IsNullOrWhiteSpace(_pinHash()) ? AdminStage.NoPin
            : AdminStage.Pin;
    }

    [RelayCommand]
    private void Digit(string? digit)
    {
        if (Stage == AdminStage.Pin && digit is { Length: 1 } && char.IsAsciiDigit(digit[0]) && PinInput.Length < MaxPinLength)
        {
            PinInput += digit;
            Error = null;
        }
    }

    [RelayCommand]
    private void Erase()
    {
        if (PinInput.Length > 0)
        {
            PinInput = PinInput[..^1];
        }
    }

    /// <summary>Checks the PIN; the fifth wrong one locks the menu.</summary>
    [RelayCommand]
    private void Enter()
    {
        if (Stage != AdminStage.Pin || PinInput.Length == 0)
        {
            return;
        }

        var right = SecretHash.Verify(PinInput, _pinHash());
        PinInput = string.Empty;

        if (right)
        {
            _failures = 0;
            Stage = AdminStage.Menu;
            return;
        }

        _failures++;

        if (_failures >= MaxAttempts)
        {
            _failures = 0;
            _lockedUntil = _now() + LockTime;
            Stage = AdminStage.Locked;
            return;
        }

        Error = T("admin.wrongPin", MaxAttempts - _failures);
    }

    [RelayCommand]
    private void LeaveSafeMode()
    {
        if (Stage == AdminStage.Menu && IsSafeMode)
        {
            LeaveSafeModeRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Restart()
    {
        if (Stage == AdminStage.Menu)
        {
            RestartRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Maintenance()
    {
        if (Stage == AdminStage.Menu)
        {
            MaintenanceRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Close()
    {
        PinInput = string.Empty;
        Error = null;
        Closed?.Invoke();
    }

    partial void OnPinInputChanged(string value) => OnPropertyChanged(nameof(PinDots));
}
