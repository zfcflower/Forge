using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Forge.App.Preferences;

/// <summary>
/// One section of the settings page. Each feature contributes its own section (view model rendered by an app-level
/// DataTemplate keyed on its type) through <see cref="SettingsSections.Add"/> from the composition root, so features do
/// not edit the settings page itself. Sections are shown in ascending <see cref="Order"/> inside their
/// <see cref="Group"/>; the title is a <see cref="Localization.Loc"/> key and the icon is a key of
/// <c>Resources/Icons.axaml</c>.
/// </summary>
internal sealed record SettingsSection(
    string Id,
    string TitleKey,
    int Order,
    object ViewModel,
    string Group = SettingsGroups.Agent,
    string? IconKey = null) : INotifyPropertyChanged
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

/// <summary>
/// A section whose body reads something when it is shown (a database, a directory, the live provider catalogue). The page
/// calls <see cref="Reload"/> when the section is selected, never on a timer, so the read happens exactly when it is seen.
/// </summary>
internal interface IReloadableSettingsSection
{
    void Reload();
}

/// <summary>Well-known section ids and orders, so features agree on placement without coordinating.</summary>
internal static class SettingsGroups
{
    /// <summary>What the agent runs on: providers, the tools and servers it may call, and the approvals in between.</summary>
    public const string Agent = "agent";

    /// <summary>How the workbench looks and behaves.</summary>
    public const string Interface = "interface";

    /// <summary>Host-level behaviour: extensions and the paths/services the shell found.</summary>
    public const string System = "system";

    /// <summary>The three group headings in page order.</summary>
    public static IReadOnlyList<string> All { get; } = [Agent, Interface, System];

    public static string TitleKey(string group) => group switch
    {
        Interface => Localization.CoreStrings.SettingsGroupInterfaceKey,
        System => Localization.CoreStrings.SettingsGroupSystemKey,
        _ => Localization.CoreStrings.SettingsGroupAgentKey,
    };
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
    public const string Network = "network";
    public const string Extensions = "extensions";
    public const string Paths = "paths";

    /// <summary>The four session-instrument sections: what a session can use and what it has used, read-only.</summary>
    public const string Approvals = "approvals";

    public const string Skills = "skills";

    public const string Mcp = "mcp";

    public const string Tools = "tools";

    /// <summary>The automations page: built-ins, user definitions and their run history (the title-bar entry opens it).</summary>
    public const string Automations = "automations";

    /// <summary>Prompt templates: commands, agents and the built-in prompt slots, with the user's overrides.</summary>
    public const string Prompts = "prompts";

    // The sections of the thinkrail-gap waves. The ids and orders are allocated here once so no wave edits this file; the
    // wave that owns a section registers it (its Composition module) and picks the group.

    /// <summary>Which models the picker lists (wave W1).</summary>
    public const string ModelsVisibility = "models-visibility";

    /// <summary>The per-provider request/token budget and the automation gap (wave W15).</summary>
    public const string RateLimit = "rate-limit";

    /// <summary>Sub-agent limits (wave W1).</summary>
    public const string Agents = "agents";

    /// <summary>Idle-workspace archiving (wave W1).</summary>
    public const string IdleArchive = "idle-archive";

    /// <summary>Notification permission guidance (wave W1).</summary>
    public const string NotificationPermission = "notification-permission";

    /// <summary>Palette sources and limits (wave W3).</summary>
    public const string Palette = "palette";

    /// <summary>Chat display: tool images, Markdown, diagrams (waves W4, W4b, W11).</summary>
    public const string Chat = "chat";

    /// <summary>File viewers: CSV, notebooks, images (wave W4).</summary>
    public const string Files = "files";

    /// <summary>The resource inspector (wave W8).</summary>
    public const string Inspector = "inspector";

    /// <summary>Cloning and creating projects (wave W9).</summary>
    public const string Projects = "projects";

    /// <summary>Update checks and channel (wave W10).</summary>
    public const string Updates = "updates";

    public const int ProviderOrder = 100;
    public const int AppearanceOrder = 200;
    public const int LanguageOrder = 300;

    /// <summary>The editor sits between the language choice and the terminal: it is a display choice like appearance, not a tool.</summary>
    public const int EditorOrder = 350;

    public const int TerminalOrder = 400;
    public const int GitOrder = 500;

    /// <summary>The notification switch sits right after git: it is the one place a window-wide preference lives.</summary>
    public const int NotificationsOrder = 550;

    public const int NetworkOrder = 580;
    public const int ExtensionsOrder = 600;
    public const int PathsOrder = 900;

    /// <summary>The instrument sections sit after the extension host and before the paths diagnostics.</summary>
    public const int ApprovalsOrder = 700;

    public const int SkillsOrder = 710;

    public const int McpOrder = 720;

    public const int ToolsOrder = 730;

    public const int AutomationsOrder = 150;

    /// <summary>After the providers (100), before the automations (150).</summary>
    public const int PromptsOrder = 130;

    public const int ModelsVisibilityOrder = 110;

    /// <summary>
    /// Right after the model list and before the sub-agent caps: the budget is a property of the provider connection, so it
    /// belongs with what the providers offer rather than with the interface below it.
    /// </summary>
    public const int RateLimitOrder = 115;

    public const int AgentsOrder = 120;
    public const int ChatOrder = 320;
    public const int FilesOrder = 360;
    public const int PaletteOrder = 380;
    public const int ProjectsOrder = 510;
    public const int NotificationPermissionOrder = 560;
    public const int IdleArchiveOrder = 570;
    public const int InspectorOrder = 740;
    public const int UpdatesOrder = 800;
}
