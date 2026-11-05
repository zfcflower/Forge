namespace Forge.App.Preferences;

/// <summary>
/// One section of the settings page. Each feature contributes its own section (view model rendered by an app-level
/// DataTemplate keyed on its type) through <see cref="SettingsSections.Add"/> from the composition root, so features do
/// not edit the settings page itself. Sections are shown in ascending <see cref="Order"/>; the title is a
/// <see cref="Localization.Loc"/> key.
/// </summary>
internal sealed record SettingsSection(string Id, string TitleKey, int Order, object ViewModel);

/// <summary>Registry of settings sections, owned by the settings view model; UI thread only.</summary>
internal sealed class SettingsSections
{
    public System.Collections.ObjectModel.ReadOnlyObservableCollection<SettingsSection> Items => throw new NotImplementedException();

    /// <summary>Adds a section, keeping <see cref="Items"/> sorted by order; a duplicate id throws.</summary>
    public void Add(SettingsSection section) => throw new NotImplementedException();
}

/// <summary>Well-known section ids and orders, so features agree on placement without coordinating.</summary>
internal static class SettingsSectionIds
{
    public const string Provider = "provider";
    public const string Appearance = "appearance";
    public const string Language = "language";
    public const string Terminal = "terminal";
    public const string Git = "git";
    public const string Extensions = "extensions";
    public const string Paths = "paths";

    public const int ProviderOrder = 100;
    public const int AppearanceOrder = 200;
    public const int LanguageOrder = 300;
    public const int TerminalOrder = 400;
    public const int GitOrder = 500;
    public const int ExtensionsOrder = 600;
    public const int PathsOrder = 900;
}
