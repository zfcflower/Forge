using System.Windows.Input;
using Avalonia.Media;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Appearance;

/// <summary>
/// The <c>appearance</c> settings section: one <see cref="FontGroupViewModel"/> for the UI font and one for the code font.
/// UI thread only. Edits go through the <see cref="AppPreferences"/> setters (validation and persistence belong to the
/// preferences foundation); external changes to the preferences are echoed back into the editors.
/// </summary>
internal sealed class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Starts loading <paramref name="catalog"/> in the background; the groups' <see cref="FontGroupViewModel.Families"/> fill in when it completes.</summary>
    public AppearanceSettingsViewModel(AppPreferences preferences, SystemFontCatalog catalog) => throw new NotImplementedException();

    public FontGroupViewModel Ui => throw new NotImplementedException();

    public FontGroupViewModel Code => throw new NotImplementedException();

    /// <summary>Completes when the font list has been loaded into both groups (for tests).</summary>
    internal Task FamiliesLoaded => throw new NotImplementedException();

    /// <summary>Unsubscribes from the preferences and <c>Loc.LanguageChanged</c>. Idempotent.</summary>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>One font group (family + size + preview + reset) bound by <c>AppearanceSettingsView</c>.</summary>
internal sealed class FontGroupViewModel : ObservableObject
{
    public const int MinSize = (int)AppPreferences.MinFontSize;
    public const int MaxSize = (int)AppPreferences.MaxFontSize;
    public const int SizeStep = 1;

    private FontGroupViewModel() => throw new NotImplementedException();

    /// <summary>Group heading in the current language.</summary>
    public string Title => throw new NotImplementedException();

    public string FamilyLabel => throw new NotImplementedException();

    public string SizeLabel => throw new NotImplementedException();

    public string ResetText => throw new NotImplementedException();

    /// <summary>Sample sentence rendered with <see cref="PreviewFamily"/>/<see cref="PreviewSize"/>.</summary>
    public string PreviewText => throw new NotImplementedException();

    /// <summary>Installed families offered by the editable box; empty until the catalog finished.</summary>
    public IReadOnlyList<string> Families => throw new NotImplementedException();

    /// <summary>The text in the family editor. Typing does not apply; <see cref="CommitFamily"/> does.</summary>
    public string FamilyText { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <summary>
    /// Writes <see cref="FamilyText"/> to the preference (on selection, Enter or focus loss), then re-reads the validated
    /// value into <see cref="FamilyText"/> (an empty text becomes the default family).
    /// </summary>
    public void CommitFamily() => throw new NotImplementedException();

    /// <summary>Bound to <c>NumericUpDown.Value</c>; setting applies immediately (null is ignored).</summary>
    public decimal? Size { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public FontFamily PreviewFamily => throw new NotImplementedException();

    public double PreviewSize => throw new NotImplementedException();

    /// <summary>Resets only this group's family and size to the defaults of <see cref="AppPreferences"/>.</summary>
    public ICommand ResetCommand => throw new NotImplementedException();
}
