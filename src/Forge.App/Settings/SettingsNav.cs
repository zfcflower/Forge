using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Media;
using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.Settings;

/// <summary>
/// One clickable row of the settings window's left column: the section it selects and the icon of its
/// <c>Resources/Icons.axaml</c> key, resolved once so the row does not look up application resources while binding.
/// </summary>
internal sealed class SettingsNavItemViewModel : ViewModels.ObservableObject
{
    private bool isSelected;

    public SettingsNavItemViewModel(SettingsSection section, bool showTitle)
    {
        ArgumentNullException.ThrowIfNull(section);
        Section = section;
        ShowTitle = showTitle;
        Icon = ResolveIcon(section.IconKey);
    }

    public SettingsSection Section { get; }

    public string Id => Section.Id;

    public string Title => Section.Title;

    /// <summary>False in the collapsed rail, where only the glyph fits (the title moves to the row's tooltip).</summary>
    public bool ShowTitle { get; }

    /// <summary>The glyph, or <c>null</c> for a section that did not name one (the row then shows text only).</summary>
    public Geometry? Icon { get; }

    public bool HasIcon => Icon is not null;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    /// <summary>Re-reads the language-dependent title after <see cref="Loc.LanguageChanged"/>.</summary>
    internal void RaiseTitle() => OnPropertyChanged(nameof(Title));

    private static Geometry? ResolveIcon(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;
    }
}

/// <summary>
/// A heading plus its sections (<c>AGENT</c> / <c>界面</c> / <c>系统</c>). The heading is a board-level grouping, not a
/// section: it has no body and cannot be selected.
/// </summary>
internal sealed class SettingsNavGroupViewModel : ViewModels.ObservableObject
{
    public SettingsNavGroupViewModel(string id, bool showTitle)
    {
        Id = id;
        ShowTitle = showTitle;
    }

    public string Id { get; }

    public string Title => Loc.Instance[SettingsGroups.TitleKey(Id)];

    public ObservableCollection<SettingsNavItemViewModel> Items { get; } = [];

    /// <summary>False in the collapsed rail, where the headings would waste the little width there is.</summary>
    public bool ShowTitle { get; }

    internal void RaiseTitle() => OnPropertyChanged(nameof(Title));
}
