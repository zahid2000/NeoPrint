using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Crypto;
using NeoPrint.Kiosk.Core;

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

    /// <summary>Neither an admin PIN (not activated) nor a service PIN (T-334): there is no way in from the screen.</summary>
    NoPin = 3,

    /// <summary>The kiosk's QR for an operator to read (T-392); the PIN is the way in without the server.</summary>
    Qr = 4,
}

/// <summary>A printer the operator may choose at the kiosk (T-392).</summary>
public sealed record AdminPrinterChoice(string Name, bool IsSelected);

/// <summary>
/// The hidden admin exit (plan sections 7.1, 7.12): five taps on the logo, the
/// kiosk's admin PIN, then what a technician on site may do - leave safe mode,
/// restart the application, or hand the machine over for maintenance.
/// </summary>
/// <remarks>
/// <para>
/// The PIN is checked against the PBKDF2 hash the backend sends in the
/// configuration; the PIN itself never leaves the admin panel. The machine's
/// own service PIN, set while it was provisioned, is accepted as well - before
/// activation and with no server it is the only way in (T-334). Five wrong ones
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

    /// <summary>How often the kiosk looks whether an operator confirmed its QR.</summary>
    public static readonly TimeSpan LoginPollEvery = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _pinHash;
    private readonly Func<string?> _servicePinHash;
    private readonly Func<DateTime> _now;
    private readonly IOperatorLogins? _logins;
    private readonly Func<KioskConfiguration?> _configuration;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private int _failures;
    private DateTime? _lockedUntil;
    private CancellationTokenSource? _login;

    /// <summary>The operator's login, once they confirmed the QR (T-392).</summary>
    private Guid? _loginId;

    [ObservableProperty]
    private string? _loginQr;

    /// <summary>In whose name the menu is open (T-392); null when it was opened by PIN.</summary>
    [ObservableProperty]
    private string? _operatorName;

    [ObservableProperty]
    private bool _canSelectPrinter;

    [ObservableProperty]
    private bool _canChangeQueue;

    [ObservableProperty]
    private bool _canPause;

    [ObservableProperty]
    private int _queueLimit;

    /// <summary>What happened to the last change made from the menu.</summary>
    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _busy;

    public ObservableCollection<AdminPrinterChoice> Printers { get; } = [];

    [ObservableProperty]
    private AdminStage _stage;

    [ObservableProperty]
    private string _pinInput = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isSafeMode;

    /// <param name="pinHash">The admin PIN hash from the kiosk's configuration; null before activation.</param>
    /// <param name="servicePinHash">The machine's service PIN hash (<c>ServicePin</c>); null when none was set.</param>
    /// <param name="logins">The operator's QR (T-392); null keeps the PIN alone.</param>
    /// <param name="configuration">The kiosk's configuration: its printers and queue size.</param>
    public AdminMenuViewModel(
        Func<string?> pinHash,
        Func<string?>? servicePinHash = null,
        Func<DateTime>? now = null,
        IOperatorLogins? logins = null,
        Func<KioskConfiguration?>? configuration = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _pinHash = pinHash;
        _servicePinHash = servicePinHash ?? (() => null);
        _now = now ?? (() => DateTime.UtcNow);
        _logins = logins;
        _configuration = configuration ?? (() => null);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The menu acts in an operator's name, not just on this machine.</summary>
    public bool HasOperator => OperatorName is not null;

    /// <summary>Without an operator the machine's own actions take the whole width.</summary>
    public int MenuSpan => HasOperator ? 1 : 2;

    public string QrTitle => T("admin.qrTitle");

    public string QrBody => T("admin.qrBody");

    public string QrWaitingText => T("admin.qrWaiting");

    public string UsePinText => T("admin.usePin");

    public string OperatorText => OperatorName is null ? string.Empty : T("admin.operator", OperatorName);

    public string PrinterTitle => T("admin.printerTitle");

    public string QueueTitle => T("admin.queueTitle");

    public string PauseOrdersText => T("admin.pauseOrders");

    public string ResumeOrdersText => T("admin.resumeOrders");

    public bool QueueIsThree => QueueLimit == 3;

    public bool QueueIsFive => QueueLimit == 5;

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

    /// <summary>
    /// Shown when the gesture is done: the operator's QR on an activated kiosk
    /// (T-392), else the PIN, or the reason there is no way in.
    /// </summary>
    public void Open(bool safeMode)
    {
        IsSafeMode = safeMode;
        PinInput = string.Empty;
        Error = null;
        Notice = null;
        ForgetOperator();

        Stage = _lockedUntil is { } until && until > _now() ? AdminStage.Locked
            : _logins is not null && !string.IsNullOrWhiteSpace(_pinHash()) ? AdminStage.Qr
            : string.IsNullOrWhiteSpace(_pinHash()) && string.IsNullOrWhiteSpace(_servicePinHash()) ? AdminStage.NoPin
            : AdminStage.Pin;

        if (Stage == AdminStage.Qr)
        {
            _ = WaitForOperatorAsync();
        }
    }

    /// <summary>
    /// Shows a QR and looks every two seconds whether an operator confirmed it;
    /// a QR whose time ran out is replaced. Without the server the PIN is the way in.
    /// </summary>
    public async Task WaitForOperatorAsync()
    {
        _login?.Cancel();
        var login = _login = new CancellationTokenSource();
        var token = login.Token;

        try
        {
            while (!token.IsCancellationRequested && Stage == AdminStage.Qr)
            {
                var started = await _logins!.StartAsync(token).ConfigureAwait(true);
                LoginQr = started.Qr;
                Error = null;

                while (!token.IsCancellationRequested && Stage == AdminStage.Qr)
                {
                    await _delay(LoginPollEvery, token).ConfigureAwait(true);
                    var state = await _logins.GetAsync(started.Id, token).ConfigureAwait(true);

                    if (state.IsApproved)
                    {
                        Approved(state);
                        return;
                    }

                    if (state.IsExpired)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The menu was closed, or the PIN chosen.
        }
        catch (Exception exception) when (exception is HttpRequestException or KioskApiException)
        {
            if (!token.IsCancellationRequested && Stage == AdminStage.Qr)
            {
                LoginQr = null;
                Error = T("admin.qrOffline");
            }
        }
    }

    private void Approved(OperatorLoginState state)
    {
        _loginId = state.Id;
        OperatorName = state.OperatorName ?? string.Empty;
        CanSelectPrinter = state.CanSelectPrinter;
        CanChangeQueue = state.CanChangeQueue;
        CanPause = state.CanPause;
        ShowConfiguration();
        Stage = AdminStage.Menu;
    }

    /// <summary>The printers and queue size as the configuration has them now.</summary>
    public void ShowConfiguration()
    {
        var configuration = _configuration();
        Printers.Clear();

        foreach (var printer in configuration?.AvailablePrinters ?? [])
        {
            Printers.Add(new AdminPrinterChoice(printer.Name, string.Equals(printer.Name, configuration!.SelectedPrinter, StringComparison.Ordinal)));
        }

        QueueLimit = configuration?.QueueDisplayLimit ?? 0;
    }

    [RelayCommand]
    private void UsePin()
    {
        _login?.Cancel();
        LoginQr = null;
        Error = null;
        Stage = string.IsNullOrWhiteSpace(_pinHash()) && string.IsNullOrWhiteSpace(_servicePinHash()) ? AdminStage.NoPin : AdminStage.Pin;
    }

    [RelayCommand]
    private Task SelectPrinterAsync(string? name)
        => ActAsync("SelectPrinter", name, null, () =>
        {
            var chosen = Printers.ToList();
            Printers.Clear();
            foreach (var printer in chosen)
            {
                Printers.Add(printer with { IsSelected = string.Equals(printer.Name, name, StringComparison.Ordinal) });
            }
        });

    [RelayCommand]
    private Task SetQueueLimitAsync(string? limit)
        => int.TryParse(limit, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? ActAsync("SetQueueLimit", null, value, () => QueueLimit = value)
            : Task.CompletedTask;

    [RelayCommand]
    private Task PauseOrdersAsync() => ActAsync("Pause", null, null, null);

    [RelayCommand]
    private Task ResumeOrdersAsync() => ActAsync("Resume", null, null, null);

    /// <summary>A change in the operator's name: the backend carries it out and audits it.</summary>
    private async Task ActAsync(string action, string? printer, int? limit, Action? done)
    {
        if (_logins is null || _loginId is not { } id || Busy)
        {
            return;
        }

        Busy = true;
        Notice = null;
        Error = null;

        try
        {
            await _logins.ActAsync(id, action, printer, limit, CancellationToken.None).ConfigureAwait(true);
            done?.Invoke();
            Notice = T("admin.saved");
        }
        catch (KioskApiException exception)
        {
            Error = T("admin.actionFailed", exception.Message);
        }
        catch (HttpRequestException)
        {
            Error = T("admin.actionFailed", T("admin.network"));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Closed: the operator's login ends, nothing more is done in their name.</summary>
    private void ForgetOperator()
    {
        _login?.Cancel();
        _login = null;
        LoginQr = null;

        if (_loginId is { } id && _logins is not null)
        {
            _ = EndQuietlyAsync(_logins, id);
        }

        _loginId = null;
        OperatorName = null;
        CanSelectPrinter = false;
        CanChangeQueue = false;
        CanPause = false;
        Printers.Clear();
    }

    private static async Task EndQuietlyAsync(IOperatorLogins logins, Guid id)
    {
        try
        {
            await logins.EndAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or KioskApiException or TaskCanceledException)
        {
            // The login runs out by itself on the server.
        }
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

        var right = SecretHash.Verify(PinInput, _pinHash()) || SecretHash.Verify(PinInput, _servicePinHash());
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
        Notice = null;
        ForgetOperator();
        Closed?.Invoke();
    }

    partial void OnPinInputChanged(string value) => OnPropertyChanged(nameof(PinDots));

    partial void OnOperatorNameChanged(string? value)
    {
        OnPropertyChanged(nameof(HasOperator));
        OnPropertyChanged(nameof(OperatorText));
        OnPropertyChanged(nameof(MenuSpan));
    }

    partial void OnQueueLimitChanged(int value)
    {
        OnPropertyChanged(nameof(QueueIsThree));
        OnPropertyChanged(nameof(QueueIsFive));
    }
}
