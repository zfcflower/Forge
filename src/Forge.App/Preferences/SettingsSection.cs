using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Forge.App.Preferences;

/// <summary>
/// One section of the settings page. Each feature contributes its own section (view model rendered by an app-level
/// DataTemplate keyed on its type) through <see cref="SettingsSections.Add"/> from the composition root, so features do
/// not edit the settings page itself. Sections are shown in ascending <see cref="Order"/>; the title is a
/// <see cref="Localization.Loc"/> key.
/// </summary>
internal sealed record SettingsSection(string Id, string TitleKey, int Order, object ViewModel) : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs TitleChanged = new(nameof(Title));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The heading in the current UI language; <see cref="RaiseTitle"/> re-reads it after a language switch.</summary>
    public string Title => Localization.Loc.Instance[TitleKey];

    /// <summary>Called by the settings view model when <see cref="Localization.Loc.LanguageChanged"/> fires.</summary>
    internal void RaiseTitle() => PropertyChanged?.Invoke(this, TitleChanged);
}

/// <summary>Registry of settings sections, owned by the settings view model; UI thread only.</summary>
internal sealed class SettingsSections
{
    private readonly ObservableCollection<SettingsSection> items = [];
    private readonly ReadOnlyObservableCollection<SettingsSection> itemsView;

    public SettingsSections() => itemsView = new ReadOnlyObservableCollection<SettingsSection>(items);

    public ReadOnlyObservableCollection<SettingsSection> Items => itemsView;

    /// <summary>Adds a section, keeping <see cref="Items"/> sorted by order; a duplicate id throws.</summary>
    public void Add(SettingsSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        foreach (var existing in items)
        {
            if (string.Equals(existing.Id, section.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Duplicate settings section id: " + section.Id);
            }
        }

        // Equal orders keep insertion order: the new section goes after every section that does not sort after it.
        var index = 0;
        while (index < items.Count && items[index].Order <= section.Order)
        {
            index++;
        }

        items.Insert(index, section);
    }
}

/// <summary>Well-known section ids and orders, so features agree on placement without coordinating.</summary>
internal static class SettingsSectionIds
{
    public const string Provider = "provider";
    public const string Appearance = "appearance";
    public const string Language = "language";
    public const string Editor = "editor";
    public const string Terminal = "terminal";
    public const string Git = "git";
    public const string Notifications = "notifications";
    public const string Extensions = "extensions";
    public const string Paths = "paths";

    /// <summary>The four session-instrument sections: what a session can use and what it has used, read-only.</summary>
    public const string Approvals = "approvals";

    public const string Skills = "skills";

    public const string Mcp = "mcp";

    public const string Tools = "tools";

    public const int ProviderOrder = 100;
    public const int AppearanceOrder = 200;
    public const int LanguageOrder = 300;

    /// <summary>The editor sits between the language choice and the terminal: it is a display choice like appearance, not a tool.</summary>
    public const int EditorOrder = 350;

    public const int TerminalOrder = 400;
    public const int GitOrder = 500;

    /// <summary>The notification switch sits right after git: it is the one place a window-wide preference lives.</summary>
    public const int NotificationsOrder = 550;

    public const int ExtensionsOrder = 600;
    public const int PathsOrder = 900;

    /// <summary>The instrument sections sit after the extension host and before the paths diagnostics.</summary>
    public const int ApprovalsOrder = 700;

    public const int SkillsOrder = 710;

    public const int McpOrder = 720;

    public const int ToolsOrder = 730;
}
