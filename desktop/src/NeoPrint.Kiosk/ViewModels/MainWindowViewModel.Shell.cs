using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The shell as the poster draws it (T-371): a compact language picker and
/// the date in the header, and what the kiosk offers on the footer (T-388).
/// The queue is the panel beside every screen again (T-389).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>How long the language list stays out without a touch.</summary>
    public static readonly TimeSpan LanguagesOpenFor = TimeSpan.FromSeconds(60);

    /// <summary>The three languages are out, under "AZ ▾".</summary>
    [ObservableProperty]
    private bool _isLanguageOpen;

    /// <summary>"AZ", "EN", "RU": the language on now, in the header.</summary>
    public string LanguageShort => Language.ToUpperInvariant();

    /// <summary>Under the clock: dates read the same in every language (docs/DESIGN_SYSTEM.md §6).</summary>
    public string DateText => Now.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public string BackText => T("common.back");

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
    private void ToggleLanguages() => IsLanguageOpen = !IsLanguageOpen;

    /// <summary>The next person does not find the last one's language list open.</summary>
    private void CloseShellIfIdle(TimeSpan idle)
    {
        if (idle > LanguagesOpenFor)
        {
            IsLanguageOpen = false;
        }
    }

    private void ShellPrinterChanged()
    {
        OnPropertyChanged(nameof(PrinterOk));
        OnPropertyChanged(nameof(PrinterStatusText));
    }
}
