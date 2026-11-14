using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Settings;

/// <summary>
/// The <c>palette</c> section: how many rows one source may list, and which directory names the content search leaves out.
/// Both editors write through to the live <see cref="AppPreferences"/>, which the palette reads when it builds a list and
/// the search reads when it starts one, so a change applies to the next keystroke - no reload, no restart.
/// </summary>
internal sealed class PaletteSettingsSectionViewModel : NotifyingObject
{
    private readonly AppPreferences preferences;
    private string excludeText;

    public PaletteSettingsSectionViewModel(AppPreferences preferences)
    {
        this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        excludeText = preferences.PaletteExcludeDirectories;
    }

    /// <summary>The section's heading; the settings page reads it as a <see cref="Loc"/> key.</summary>
    public static string SectionTitle => PaletteStrings.SettingsTitle;

    /// <summary>Rows one source may list; the preference clamps it, and the bounds are what the editor allows.</summary>
    public int ResultLimit
    {
        get => preferences.PalettePerSourceLimit;
        set
        {
            if (preferences.PalettePerSourceLimit == value)
            {
                return;
            }

            preferences.PalettePerSourceLimit = value;
            OnPropertyChanged();
        }
    }

    public double MinimumLimit => AppPreferences.MinPalettePerSourceLimit;

    public double MaximumLimit => AppPreferences.MaxPalettePerSourceLimit;

    /// <summary>
    /// Comma-separated directory names the content search skips; empty leaves it to <c>.gitignore</c>. The editor shows the
    /// text as typed while the preference stores it trimmed: reading the trimmed value back would strip the space the user
    /// just typed (the text box is bound per keystroke), so the section keeps its own copy of the raw text.
    /// </summary>
    public string ExcludeDirectories
    {
        get => excludeText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(excludeText, value, StringComparison.Ordinal))
            {
                return;
            }

            excludeText = value;
            preferences.PaletteExcludeDirectories = value;
            OnPropertyChanged();
        }
    }
}
