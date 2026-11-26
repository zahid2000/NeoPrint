using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Kiosk.Core;

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

    public string FeatureSizesTitle => Brand("featureSizes");

    public string FeatureSizesHint => Brand("featureSizesHint");

    public string FeatureColorTitle => Brand("featureColor");

    public string FeatureColorHint => Brand("featureColorHint");

    public string FeatureFastTitle => Brand("featureFast");

    public string FeatureFastHint => Brand("featureFastHint");

    public string FeatureSafeTitle => Brand("featureSafe");

    public string FeatureSafeHint => Brand("featureSafeHint");

    /// <summary>How the panel arranged the welcome screen (T-441), else the kiosk's own.</summary>
    private KioskScreenLayout ScreenLayout => KioskScreenLayout.Effective(_branding?.Layout);

    /// <summary>The column of each way in, in the order the panel set.</summary>
    public int UsbColumn => ScreenLayout.ColumnOf("usb");

    public int PhoneColumn => ScreenLayout.ColumnOf("phone");

    /// <summary>The picture on the left of the big line: column 0, else 2 (the text is column 1).</summary>
    public bool HeroImageLeft => ScreenLayout.ImageLeft;

    public int HeroArtColumn => HeroImageLeft ? 0 : 2;

    /// <summary>What the footer says the kiosk offers: the ones the panel shows, in its order.</summary>
    public IReadOnlyList<FooterFeature> FooterFeatures
        => [.. (ScreenLayout.Features ?? KioskScreenLayout.FeatureKeys).Select(key => new FooterFeature(key, Brand(key), Brand(key + "Hint")))];

    /// <summary>What the panel's words reach (T-387): told again when a configuration brings new ones.</summary>
    private static readonly string[] BrandedProperties =
    [
        nameof(HeroTitle), nameof(HeroSubtitle), nameof(UsbCardTitle), nameof(UsbCardHint),
        nameof(PhoneCardTitle), nameof(PhoneCardHint), nameof(FeatureSizesTitle),
        nameof(FeatureSizesHint), nameof(FeatureColorTitle), nameof(FeatureColorHint), nameof(FeatureFastTitle),
        nameof(FeatureFastHint), nameof(FeatureSafeTitle), nameof(FeatureSafeHint), nameof(Hotline),
        nameof(UsbColumn), nameof(PhoneColumn), nameof(HeroImageLeft), nameof(HeroArtColumn),
        nameof(FooterFeatures),
    ];

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

/// <summary>One of the footer's features (T-441): its key (for the icon) and its words in the language on now.</summary>
public sealed record FooterFeature(string Key, string Title, string Hint);
