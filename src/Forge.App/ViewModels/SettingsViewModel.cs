using System.Collections.ObjectModel;
using Avalonia.Controls;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Settings;

namespace Forge.App.ViewModels;

/// <summary>
/// The settings surface: the registry of sections (<see cref="SettingsSections"/>) and the left column that lists them,
/// grouped into <c>AGENT</c> / <c>界面</c> / <c>系统</c>. The window itself is <see cref="SettingsWindow"/>, opened by the
/// shell; this type owns what a page owns — the selection, the navigation's collapsed state and the reload of the shell's
/// own diagnostics rows.
/// <para>Section bodies are rendered by the app-level DataTemplate for their view model type, so a feature adds its
/// settings without editing the window. Section headings are <see cref="Loc"/> keys and refresh on
/// <see cref="Loc.LanguageChanged"/>.</para>
/// </summary>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Below this window width the section column folds into an icon rail; the user can also fold it by hand.</summary>
    public const double AutoCollapseWidth = 880;

    /// <summary>Width of the full section column, in DIPs.</summary>
    public const double ExpandedNavWidth = 240;

    /// <summary>Width of the collapsed icon rail, in DIPs.</summary>
    public const double CollapsedNavWidth = 48;

    private readonly AppComposition composition;
    private readonly PathsSettingsSectionViewModel paths;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private bool isOpen;
    private bool autoCollapsed;

    /// <summary>The user's own choice: <c>null</c> follows the window width, otherwise it wins over it.</summary>
    private bool? manualCollapsed;
    private SettingsSection? selectedSection;

    public SettingsViewModel(AppComposition composition)
    {
        this.composition = composition;

        paths = new PathsSettingsSectionViewModel();

        // The registry belongs to the composition root (that is where features append their sections); the shell only
        // guarantees that its own diagnostics section is there.
        Sections = composition.Settings;
        if (!Sections.Items.Any(static section => string.Equals(section.Id, SettingsSectionIds.Paths, StringComparison.Ordinal)))
        {
            Sections.Add(new SettingsSection(
                SettingsSectionIds.Paths,
                CoreStrings.SettingsSectionPaths,
                SettingsSectionIds.PathsOrder,
                paths,
                SettingsGroups.System,
                "IconFolder"));
        }

        NavGroups = [];
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RebuildNav();
        SelectedSection = Sections.Items.FirstOrDefault();
        Rebuild();
    }

    /// <summary>Every registered section, ordered by each section's <see cref="SettingsSection.Order"/>.</summary>
    public SettingsSections Sections { get; }

    /// <summary>The left column: one group per <see cref="SettingsGroups"/> entry that has sections.</summary>
    public ObservableCollection<SettingsNavGroupViewModel> NavGroups { get; }

    /// <summary>The section shown in the right-hand pane; the left column highlights it.</summary>
    public SettingsSection? SelectedSection
    {
        get => selectedSection;
        set
        {
            if (!Set(ref selectedSection, value))
            {
                return;
            }

            foreach (var group in NavGroups)
            {
                foreach (var item in group.Items)
                {
                    item.IsSelected = ReferenceEquals(item.Section, value);
                }
            }

            if (isOpen)
            {
                RevealSelected();
            }
        }
    }

    /// <summary>Whether the settings window is on screen; the shell keeps it in step with the window's lifetime.</summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (Set(ref isOpen, value) && value)
            {
                RevealSelected();
            }
        }
    }

    /// <summary>
    /// The section column folded into an icon rail. A narrowed window folds it by itself; <see cref="ToggleNav"/> records an
    /// explicit choice that then wins in both directions, so the toggle can also expand the rail inside a narrow window
    /// instead of offering a button that does nothing.
    /// </summary>
    public bool IsNavCollapsed => manualCollapsed ?? autoCollapsed;

    /// <summary>The left column's width as a grid length, so the window can bind a column definition to it.</summary>
    public GridLength NavWidth => new(IsNavCollapsed ? CollapsedNavWidth : ExpandedNavWidth);

    /// <summary>Headings are meaningless in the rail and are hidden there.</summary>
    public bool ShowNavTitles => !IsNavCollapsed;

    /// <summary>The expanded mirror, so the title row can show the icon of the direction the button goes.</summary>
    public bool IsNavExpanded => !IsNavCollapsed;

    public string NavToggleTip => Loc.Instance[IsNavCollapsed ? CoreStrings.SettingsExpandNav : CoreStrings.SettingsCollapseNav];

    public string Title => Loc.Instance[CoreStrings.SettingsTitle];

    public string ReloadText => Loc.Instance[CoreStrings.SettingsReload];

    public string CloseText => Loc.Instance[CoreStrings.SettingsClose];

    /// <summary>Raised when the window's close affordance is used; the window closes itself and clears <see cref="IsOpen"/>.</summary>
    public event EventHandler? CloseRequested;

    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The title row's refresh: re-read <c>settings.json</c> and rebuild the shell's rows, so a file edited by hand is
    /// picked up without restarting. The read runs on the pool — the click is on the UI thread.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await composition.ReloadProviderAsync(cancellationToken).ConfigureAwait(true);
        RevealSelected();
    }

    /// <summary>
    /// The window's client width changed: fold the column below <see cref="AutoCollapseWidth"/> and unfold it above, without
    /// disturbing a fold the user asked for.
    /// </summary>
    public void UpdateAvailableWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return;
        }

        var collapsed = width < AutoCollapseWidth;
        if (collapsed == autoCollapsed)
        {
            return;
        }

        var wasCollapsed = IsNavCollapsed;
        autoCollapsed = collapsed;
        if (IsNavCollapsed != wasCollapsed)
        {
            RaiseNavState();
        }
    }

    /// <summary>
    /// The title row's toggle: records the user's choice for the column's state. It flips whatever is on screen, so in a
    /// narrow window it can expand the rail as well as fold it.
    /// </summary>
    public void ToggleNav()
    {
        manualCollapsed = !IsNavCollapsed;
        RaiseNavState();
    }

    /// <summary>
    /// Selects one registered section by id: the click target of a session instrument counter. An id no feature registered
    /// leaves the current selection alone, so a counter whose section is missing never blanks the page.
    /// </summary>
    public void SelectSection(string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
        {
            return;
        }

        foreach (var section in Sections.Items)
        {
            if (string.Equals(section.Id, sectionId, StringComparison.Ordinal))
            {
                SelectedSection = section;
                return;
            }
        }
    }

    public void Rebuild()
    {
        paths.Paths.Clear();
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathFile], composition.Paths.SettingsPath));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathData], composition.Paths.UserDataDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathWorkspace], composition.WorkspaceDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathGeneralWorkspace], composition.Paths.GeneralWorkspaceDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathHostData], composition.Paths.HostDataDirectory));
        paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathPlugins], composition.Paths.PluginsDirectory));

        if (composition.PreferencesProblem is { Length: > 0 } preferencesProblem)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathPreferences], preferencesProblem));
        }

        var hostProblem = composition.HostProblem;
        if (!hostProblem.IsEmpty)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathHost], hostProblem.Resolve()));
        }

        if (composition.ApprovalsDatabasePath is { Length: > 0 } approvalsDatabase)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathApprovals], approvalsDatabase));
        }

        if (composition.Mcp is { } mcp)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathMcp], ShellStrings.McpDetail(mcp.ConfiguredServers, mcp.ConfigDiagnostics)));
        }

        if (composition.Editor is { } editor)
        {
            paths.Paths.Add(new SettingLine(Loc.Instance[CoreStrings.SettingsPathEditor], ShellStrings.EditorDetail(editor.LanguageCount, editor.GrammarCount, editor.Problem)));
        }
    }

    /// <summary>Drops the language subscription; the window disposes the settings page with itself.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    /// <summary>
    /// Rebuilds the left column from the registry, keeping the selection. Called at construction and on every language
    /// switch (the headings and titles come from <see cref="Loc"/>).
    /// </summary>
    private void RebuildNav()
    {
        var selectedId = selectedSection?.Id;
        NavGroups.Clear();
        foreach (var groupId in SettingsGroups.All)
        {
            var group = new SettingsNavGroupViewModel(groupId, ShowNavTitles);
            foreach (var section in Sections.Items)
            {
                if (string.Equals(section.Group, groupId, StringComparison.Ordinal))
                {
                    var item = new SettingsNavItemViewModel(section, ShowNavTitles)
                    {
                        IsSelected = string.Equals(section.Id, selectedId, StringComparison.Ordinal),
                    };
                    group.Items.Add(item);
                }
            }

            if (group.Items.Count > 0)
            {
                NavGroups.Add(group);
            }
        }
    }

    private void RaiseNavState()
    {
        OnPropertyChanged(nameof(IsNavCollapsed));
        OnPropertyChanged(nameof(IsNavExpanded));
        OnPropertyChanged(nameof(NavWidth));
        OnPropertyChanged(nameof(ShowNavTitles));
        OnPropertyChanged(nameof(NavToggleTip));

        // The headings are part of the column's items, so the rail rebuilds them rather than hiding each one by binding.
        RebuildNav();
    }

    /// <summary>
    /// Something is about to be looked at: rebuild the shell's own rows and let a section whose content is a read-only list
    /// go and read its source. Reloading here rather than on every counter click keeps the database and directory reads off
    /// the page except when it is actually shown.
    /// </summary>
    private void RevealSelected()
    {
        Rebuild();
        if (SelectedSection?.ViewModel is IReloadableSettingsSection reloadable)
        {
            reloadable.Reload();
        }
    }

    /// <summary>
    /// Text that caches a language-dependent value has to re-read it: the headings through <see cref="SettingsSection.Title"/>,
    /// the column's group titles and the path rows' labels (rebuilt by <see cref="Rebuild"/>).
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a settings page whose window is gone (a test host): rebuilding
        // its rows there would touch bound controls off their thread.
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ReloadText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(NavToggleTip));

        foreach (var section in Sections.Items)
        {
            section.RaiseTitle();
        }

        RebuildNav();
        Rebuild();
    }
}

/// <summary>The <c>paths</c> section: the data root, the workspace and the services the shell found.</summary>
internal sealed class PathsSettingsSectionViewModel
{
    public ObservableCollection<SettingLine> Paths { get; } = [];
}

/// <summary>One label/value row of the settings panel.</summary>
internal sealed record SettingLine(string Label, string Value);
