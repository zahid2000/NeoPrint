using CommunityToolkit.Mvvm.ComponentModel;

using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// Base for the kiosk view models.
/// </summary>
/// <remarks>
/// Carries the current language, because the kiosk switches language from the
/// header on a screen that is already on display (plan section 7.2).
/// </remarks>
public abstract class ViewModelBase : ObservableObject
{
    private string _language = Strings.DefaultLanguage;

    /// <summary>The language this screen renders in.</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (!Strings.IsSupported(value))
            {
                return;
            }

            if (SetProperty(ref _language, value))
            {
                OnLanguageChanged();
            }
        }
    }

    /// <summary>Looks up a UI string in the current language.</summary>
    protected string T(string key) => Strings.Get(Language, key);

    /// <summary>Looks up and formats a UI string in the current language.</summary>
    protected string T(string key, params object?[] arguments) => Strings.Get(Language, key, arguments);

    /// <summary>
    /// Called when the language changes, so a screen can re-raise the
    /// properties that hold translated text.
    /// </summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);
}
