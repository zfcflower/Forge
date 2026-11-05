using System.ComponentModel;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// The <c>language</c> settings section: "follow the system", Chinese or English, written straight to
/// <see cref="AppPreferences.Language"/>. The composition root owns the rest of the path - its
/// <c>OnPreferencesChanged</c> pushes the new value into <see cref="Loc"/>, which refreshes every bound text - so this
/// class never detects the system language itself (it asks <see cref="Loc"/>) and never touches the settings file.
/// <para>The three choices are radio buttons bound to <see cref="IsSystem"/> / <see cref="IsChinese"/> /
/// <see cref="IsEnglish"/>; a choice that is checked writes the preference and a choice that is unchecked is ignored, so
/// the group behaves like a radio group without a converter. Text is read from <see cref="Loc"/> in the getters and the
/// properties are re-raised on <see cref="Loc.LanguageChanged"/>, so a switch updates the section in place.</para>
/// <para>A language keeps the name the section gives it (<c>中文</c> / <c>Chinese</c>, <c>English</c> /
/// <c>English</c>), so the English option stays readable when the UI is Chinese. Long-lived: the section lives as long as
/// the settings page, and <see cref="Dispose"/> exists so a test (and the composition root, should it ever rebuild the
/// page) can drop the subscriptions instead of leaking them.</para>
/// </summary>
internal sealed class LanguageSettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppPreferences preferences;
    private bool disposed;

    public LanguageSettingsViewModel(AppPreferences preferences)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        this.preferences.PropertyChanged += OnPreferencesChanged;
    }

    /// <summary>The stored choice. Setting it applies immediately; <see cref="PreferencesStore"/> persists it later.</summary>
    public UiLanguage Language
    {
        get => preferences.Language;
        set
        {
            if (preferences.Language == value)
            {
                return;
            }

            // UI thread only: the setter raises PropertyChanged, which the composition root turns into a Loc update and
            // the store turns into a debounced file write.
            preferences.Language = value;
            RaiseSelection();
        }
    }

    /// <summary>True when the UI follows the system language; checking it selects <see cref="UiLanguage.System"/>.</summary>
    public bool IsSystem
    {
        get => preferences.Language == UiLanguage.System;
        set => Select(UiLanguage.System, value);
    }

    /// <summary>True when the UI is Chinese; checking it selects <see cref="UiLanguage.Chinese"/>.</summary>
    public bool IsChinese
    {
        get => preferences.Language == UiLanguage.Chinese;
        set => Select(UiLanguage.Chinese, value);
    }

    /// <summary>True when the UI is English; checking it selects <see cref="UiLanguage.English"/>.</summary>
    public bool IsEnglish
    {
        get => preferences.Language == UiLanguage.English;
        set => Select(UiLanguage.English, value);
    }

    /// <summary>"Follow system".</summary>
    public string SystemText => LanguageStrings.SystemLabel;

    /// <summary>
    /// The language "follow the system" currently resolves to, named the way the section names it: "中文" in a Chinese
    /// UI, "Chinese" in an English one (the English choice is always "English"). Taken from <see cref="Loc.SystemLanguage"/>
    /// - the single place that resolves the system - so it stays the same whichever choice is selected.
    /// </summary>
    public string ResolvedLanguage => Name(Loc.SystemLanguage);

    /// <summary>"Follow system (currently Chinese)": the explanation under the first choice.</summary>
    public string SystemResolvedText => LanguageStrings.SystemResolvedText(ResolvedLanguage);

    /// <summary>The name of the Chinese language.</summary>
    public string ChineseText => LanguageStrings.ChineseName;

    /// <summary>The name of the English language.</summary>
    public string EnglishText => LanguageStrings.EnglishName;

    /// <summary>The grey line that says what the setting does.</summary>
    public string Hint => LanguageStrings.HintText;

    /// <summary>Drops the subscriptions; a disposed section no longer follows the language or the preference.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        preferences.PropertyChanged -= OnPreferencesChanged;
    }

    private static string Name(UiLanguage language) =>
        language == UiLanguage.Chinese ? LanguageStrings.ChineseName : LanguageStrings.EnglishName;

    /// <summary>A radio button that became unchecked must not write the preference - only the checked one is the choice.</summary>
    private void Select(UiLanguage language, bool isChecked)
    {
        if (isChecked)
        {
            Language = language;
        }
    }

    /// <summary>
    /// The texts are read from <see cref="Loc"/>, so a switch only needs the properties re-raised. The resolution
    /// sentence is among them: its wording follows the UI language even when the language it names does not.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SystemText));
        OnPropertyChanged(nameof(ResolvedLanguage));
        OnPropertyChanged(nameof(SystemResolvedText));
        OnPropertyChanged(nameof(ChineseText));
        OnPropertyChanged(nameof(EnglishText));
        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>
    /// The choice itself can change without a language switch: "follow the system" while the system already resolves to
    /// the language on screen leaves <see cref="Loc.Language"/> unchanged, so the radio group has to refresh from the
    /// preference, not from <see cref="Loc.LanguageChanged"/>.
    /// </summary>
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AppPreferences.Language), StringComparison.Ordinal))
        {
            RaiseSelection();
        }
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(IsChinese));
        OnPropertyChanged(nameof(IsEnglish));
    }
}
