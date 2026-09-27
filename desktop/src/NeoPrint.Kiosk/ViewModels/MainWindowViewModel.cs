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

    /// <summary>
    /// Screen 15: the backend cannot be reached but the cached configuration is
    /// still fresh. Paid work keeps printing; no new payment is taken.
    /// </summary>
    Degraded = 4,

    /// <summary>Screen 16: no connection, or the cache has gone stale.</summary>
    Offline = 5,
}

/// <summary>One line in the queue panel (plan section 7.2).</summary>
/// <param name="Position">Place in the queue, from 1.</param>
/// <param name="JobCode">The six-digit code the customer was given.</param>
/// <param name="OwnerInitial">First letter of the owner's name, or empty when anonymous.</param>
/// <param name="Pages">Page count.</param>
/// <param name="IsPrinting">Whether this job is on the printer now.</param>
public sealed record QueueEntry(int Position, string JobCode, string OwnerInitial, int Pages, bool IsPrinting)
{
    /// <summary>"12 səhifə", in the kiosk's language (Localization.Formats).</summary>
    public string PagesText { get; init; } = string.Empty;
}

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

    /// <summary>
    /// The active printer, as the configuration named it.
    /// </summary>
    /// <remarks>
    /// Kept so that coming back online returns to the right screen: a kiosk
    /// with no printer chosen belongs on screen 14, not the home screen.
    /// </remarks>
    [ObservableProperty]
    private string? _selectedPrinterName;

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

    public string DegradedTitle => T("degraded.title");

    public string DegradedBody => T("degraded.body");

    public string OfflineTitle => T("offline.title");

    public string OfflineBody => T("offline.body");

    public string HomeTitle => T("home.title");

    public string PrintFromUsbText => T("home.printFromUsb");

    public string ScanMyQrText => T("home.scanMyQr");

    public string CancelByCodeText => T("home.cancelByCode");

    /// <summary>The wall clock, rendered in Asia/Baku (plan section 2.1).</summary>
    public string ClockText => Localization.Formats.Time(Now);

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

    /// <summary>
    /// Applies the configuration the backend sent (plan section 7.11).
    /// </summary>
    /// <remarks>
    /// Everything the shell shows about this kiosk comes from here, because the
    /// build itself carries none of it.
    /// </remarks>
    public void ApplyConfiguration(KioskConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        KioskName = configuration.Name;
        KioskCode = configuration.Code;
        QueueDisplayLimit = configuration.QueueDisplayLimit;
        SelectedPrinterName = configuration.SelectedPrinter;

        // A kiosk with no printer chosen yet waits on screen 14 rather than
        // offering to print (plan section 7.3).
        Screen = configuration.SelectedPrinter is null
            ? KioskScreen.NotConfigured
            : KioskScreen.Home;
    }

    /// <summary>
    /// Shows what the kiosk may actually do (plan section 7.3).
    /// </summary>
    /// <param name="hasConfiguration">
    /// Whether a configuration has been fetched at all. Without one there is
    /// nothing to work from, whatever the link is doing.
    /// </param>
    public void ApplyAvailability(KioskAvailability availability, bool hasConfiguration)
    {
        if (!hasConfiguration)
        {
            Screen = KioskScreen.Offline;
            return;
        }

        Screen = availability switch
        {
            KioskAvailability.Degraded => KioskScreen.Degraded,
            KioskAvailability.Offline => KioskScreen.Offline,

            // Back online: the home screen unless a printer still has to be
            // chosen.
            _ when Screen is KioskScreen.Degraded or KioskScreen.Offline =>
                SelectedPrinterName is null ? KioskScreen.NotConfigured : KioskScreen.Home,

            // Online and already on a working screen: leave it where it is, so
            // a heartbeat does not interrupt somebody mid-order.
            _ => Screen,
        };
    }

    /// <summary>Replaces the queue, trimmed to the display limit.</summary>
    public void SetQueue(IEnumerable<QueueEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Queue.Clear();

        foreach (var entry in entries.Take(QueueDisplayLimit))
        {
            Queue.Add(entry with { PagesText = Localization.Formats.Pages(entry.Pages, Language) });
        }
    }

    protected override void OnLanguageChanged()
    {
        Activation.Language = Language;
        base.OnLanguageChanged();

        // The queue lines carry text of their own; word them again.
        SetQueue([.. Queue]);
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
