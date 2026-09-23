using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>Which screen the central area is showing (plan section 7.3).</summary>
public enum KioskScreen
{
    /// <summary>Shown while the kiosk works out what it is.</summary>
    Starting = 0,

    /// <summary>Screen 0: not activated yet.</summary>
    Activation = 1,

    /// <summary>Screen 1.</summary>
    Home = 2,

    /// <summary>Screen 14: activated, but no printer selected.</summary>
    NotConfigured = 3,

    /// <summary>Screen 15.</summary>
    Offline = 4,
}

/// <summary>One line in the queue panel (plan section 7.2).</summary>
/// <param name="Position">Place in the queue, from 1.</param>
/// <param name="JobCode">The six-digit code the customer was given.</param>
/// <param name="OwnerInitial">First letter of the owner's name, or empty when anonymous.</param>
/// <param name="Pages">Page count.</param>
/// <param name="IsPrinting">Whether this job is on the printer now.</param>
public sealed record QueueEntry(int Position, string JobCode, string OwnerInitial, int Pages, bool IsPrinting);

/// <summary>
/// The shell: header, footer and queue panel, with one changing central area
/// (plan section 7.2).
/// </summary>
/// <remarks>
/// The header, the footer and the queue live here rather than on each screen,
/// so the kiosk QR and code stay visible everywhere and a dialog cannot cover
/// them (plan sections 7.2, 2.9).
/// </remarks>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private KioskScreen _screen = KioskScreen.Starting;

    [ObservableProperty]
    private string? _kioskName;

    [ObservableProperty]
    private string? _kioskCode;

    [ObservableProperty]
    private DateTime _now = DateTime.Now;

    [ObservableProperty]
    private int _queueDisplayLimit = 5;

    [ObservableProperty]
    private string? _hotline;

    public MainWindowViewModel()
    {
        Activation = new ActivationViewModel { Language = Language };
    }

    /// <summary>The activation screen, kept alive so its countdown keeps running.</summary>
    public ActivationViewModel Activation { get; }

    /// <summary>The queue, trimmed to the configured display limit.</summary>
    public ObservableCollection<QueueEntry> Queue { get; } = [];

    /// <summary>The languages the header offers.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        .. Strings.SupportedLanguages.Select(code => new LanguageOption(code, Strings.LanguageNames[code])),
    ];

    /// <summary>
    /// The kiosk QR, shown only in the footer and on every screen
    /// (plan section 2.9).
    /// </summary>
    public string? KioskQr => KioskCode is null ? null : $"neoprint://kiosk/{KioskCode}";

    /// <summary>
    /// Whether the footer shows the kiosk QR. An unactivated kiosk has no code
    /// yet, so the activation screen takes that space instead
    /// (plan section 7.3).
    /// </summary>
    public bool ShowKioskQr => Screen != KioskScreen.Activation && KioskCode is not null;

    public string QueueTitle => T("queue.title");

    public string QueueEmptyText => T("queue.empty");

    public string StartingText => T("app.starting");

    public string NotConfiguredTitle => T("notConfigured.title");

    public string NotConfiguredBody => T("notConfigured.body");

    public string OfflineTitle => T("offline.title");

    public string OfflineBody => T("offline.body");

    public string HomeTitle => T("home.title");

    public string PrintFromUsbText => T("home.printFromUsb");

    public string ScanMyQrText => T("home.scanMyQr");

    public string CancelByCodeText => T("home.cancelByCode");

    /// <summary>The wall clock, rendered in Asia/Baku (plan section 2.1).</summary>
    public string ClockText => Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Switches the interface language from the header.</summary>
    [RelayCommand]
    private void SelectLanguage(string? code)
    {
        if (code is not null)
        {
            Language = code;
        }
    }

    /// <summary>Applies the enrollment state and moves to the right screen.</summary>
    public void Apply(EnrollmentState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Activation.Apply(state);

        Screen = state.Lifecycle switch
        {
            KioskLifecycle.Active => KioskScreen.NotConfigured,
            KioskLifecycle.Unregistered => KioskScreen.Starting,
            _ => KioskScreen.Activation,
        };

        if (state.KioskCode is not null)
        {
            KioskCode = state.KioskCode;
        }
    }

    /// <summary>Replaces the queue, trimmed to the display limit.</summary>
    public void SetQueue(IEnumerable<QueueEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Queue.Clear();

        foreach (var entry in entries.Take(QueueDisplayLimit))
        {
            Queue.Add(entry);
        }
    }

    protected override void OnLanguageChanged()
    {
        Activation.Language = Language;
        base.OnLanguageChanged();
    }

    partial void OnKioskCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(KioskQr));
        OnPropertyChanged(nameof(ShowKioskQr));
    }

    partial void OnScreenChanged(KioskScreen value) => OnPropertyChanged(nameof(ShowKioskQr));

    partial void OnNowChanged(DateTime value) => OnPropertyChanged(nameof(ClockText));

    /// <param name="Code">Language code.</param>
    /// <param name="Name">Name as shown in the picker.</param>
    public sealed record LanguageOption(string Code, string Name);
}
