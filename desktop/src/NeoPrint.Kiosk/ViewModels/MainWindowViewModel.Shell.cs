using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The shell as the poster draws it (T-371): a compact language picker and
/// the date in the header, the queue as a screen of its own opened from the
/// footer, and what the kiosk offers along the bottom of the welcome screen.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>How long the queue screen stays without a touch.</summary>
    public static readonly TimeSpan QueueScreenFor = TimeSpan.FromSeconds(60);

    /// <summary>"Çap növbəsi" is open over the screen under it.</summary>
    [ObservableProperty]
    private bool _isQueueOpen;

    /// <summary>The three languages are out, under "AZ ▾".</summary>
    [ObservableProperty]
    private bool _isLanguageOpen;

    /// <summary>"AZ", "EN", "RU": the language on now, in the header.</summary>
    public string LanguageShort => Language.ToUpperInvariant();

    /// <summary>Under the clock: dates read the same in every language (docs/DESIGN_SYSTEM.md §6).</summary>
    public string DateText => Now.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public string BackText => T("common.back");

    public string QueueScreenTitle => T("queue.screenTitle");

    public string QueueScreenHint => T("queue.screenHint");

    /// <summary>"Növbə: 3" on the footer's button, "Növbə yoxdur" when there is none.</summary>
    public string QueueButtonText => QueueSummaryText;

    /// <summary>The printer takes orders now: the footer's green "Printer hazırdır".</summary>
    public bool PrinterOk => PrinterProblem is null && !IsMaintenance;

    /// <summary>"Printer hazırdır", or why it is not.</summary>
    public string PrinterStatusText => PrinterOk ? ReadyText : T("home.notReady");

    public string FeatureSizesTitle => T("home.featureSizes");

    public string FeatureSizesHint => T("home.featureSizesHint");

    public string FeatureColorTitle => T("home.featureColor");

    public string FeatureColorHint => T("home.featureColorHint");

    public string FeatureFastTitle => T("home.featureFast");

    public string FeatureFastHint => T("home.featureFastHint");

    public string FeatureSafeTitle => T("home.featureSafe");

    public string FeatureSafeHint => T("home.featureSafeHint");

    [RelayCommand]
    private void OpenQueue()
    {
        IsLanguageOpen = false;
        IsQueueOpen = true;
    }

    [RelayCommand]
    private void CloseQueue() => IsQueueOpen = false;

    [RelayCommand]
    private void ToggleLanguages() => IsLanguageOpen = !IsLanguageOpen;

    /// <summary>The next person does not find the last one's queue or language list open.</summary>
    private void CloseShellIfIdle(TimeSpan idle)
    {
        if (idle > QueueScreenFor)
        {
            IsQueueOpen = false;
            IsLanguageOpen = false;
        }
    }

    private void ShellPrinterChanged()
    {
        OnPropertyChanged(nameof(PrinterOk));
        OnPropertyChanged(nameof(PrinterStatusText));
    }
}
