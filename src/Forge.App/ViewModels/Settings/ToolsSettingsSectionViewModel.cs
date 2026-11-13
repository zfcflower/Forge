using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.ViewModels.Settings;

/// <summary>One tool on the Tools page. A built-in tool that may be switched off carries a live switch on the preferences.</summary>
internal sealed class ToolRowViewModel : ObservableObject
{
    private readonly AppPreferences? preferences;
    private bool isEnabled;

    public ToolRowViewModel(ToolDescriptor descriptor, AppPreferences? preferences)
    {
        Descriptor = descriptor;
        this.preferences = preferences;
        CanToggle = descriptor.Origin == ToolOrigin.BuiltIn && descriptor.CanDisable && preferences is not null;
        isEnabled = preferences?.IsToolEnabled(descriptor.Name) ?? true;
        Detail = descriptor.Origin != ToolOrigin.BuiltIn
            ? (descriptor.Source is { Length: > 0 } source ? source + " · " : string.Empty) + InstrumentsStrings.ToolsFollowsSource
            : descriptor.CanDisable ? string.Empty : InstrumentsStrings.ToolsRequired;
    }

    public ToolDescriptor Descriptor { get; }

    public string Name => Descriptor.Name;

    public string Detail { get; }

    public bool HasDetail => Detail.Length > 0;

    /// <summary>True for a built-in tool the user may switch off; the others show no switch.</summary>
    public bool CanToggle { get; }

    /// <summary>The switch: writes through to <see cref="AppPreferences.ToolsDisabled"/>, which the next turn applies.</summary>
    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (!CanToggle || !Set(ref isEnabled, value))
            {
                return;
            }

            preferences!.SetToolEnabled(Name, value);
        }
    }
}

/// <summary>One group of the Tools page: a built-in group, the extension tools or the MCP tools.</summary>
internal sealed record ToolGroupViewModel(string Title, IReadOnlyList<ToolRowViewModel> Tools);

/// <summary>
/// The <c>tools</c> section: the active session's tool catalog grouped by origin. Built-in groups come first, in a fixed
/// order, each tool with a switch unless another feature needs it; extension and MCP tools follow, read-only, because their
/// plugin or server is where they are switched. Switches write <see cref="AppPreferences.ToolsDisabled"/>; every session
/// applies it before its next turn.
/// </summary>
internal sealed class ToolsSettingsSectionViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    private static readonly string[] BuiltInOrder =
    [
        ToolGroups.Files,
        ToolGroups.Git,
        ToolGroups.PullRequests,
        ToolGroups.Web,
        ToolGroups.Session,
        ToolGroups.Agents,
        ToolGroups.Retrieval,
        ToolGroups.Extensions,
    ];

    private readonly AppComposition composition;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private string inventoryText = string.Empty;
    private string emptyText = string.Empty;
    private bool disposed;

    public ToolsSettingsSectionViewModel(AppComposition composition)
    {
        this.composition = composition;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Hint => InstrumentsStrings.ToolsSectionHint;

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    public bool HasGroups => Groups.Count > 0;

    public string EmptyText
    {
        get => emptyText;
        private set => Set(ref emptyText, value);
    }

    /// <summary>What the extension host registered for the whole app (plugins, plugin tools, commands).</summary>
    public string InventoryText
    {
        get => inventoryText;
        private set => SetAndNotify(ref inventoryText, value, nameof(HasInventory));
    }

    public bool HasInventory => inventoryText.Length > 0;

    public void Reload()
    {
        InventoryText = composition.Inventory is { } inventory
            ? InstrumentsStrings.ToolsInventory(inventory.PluginCount, inventory.ToolCount, inventory.CommandCount)
            : string.Empty;

        Groups.Clear();
        foreach (var group in Build(composition.ActiveSessionToolCatalog() ?? [], composition.Preferences))
        {
            Groups.Add(group);
        }

        EmptyText = InstrumentsStrings.ToolsSectionNoSession;
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasGroups));
    }

    /// <summary>Groups the catalog: built-in groups in <see cref="BuiltInOrder"/> (unknown ones after), then extension, then MCP.</summary>
    internal static IReadOnlyList<ToolGroupViewModel> Build(IReadOnlyList<ToolDescriptor> catalog, AppPreferences? preferences)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builtIn = new Dictionary<string, List<ToolRowViewModel>>(StringComparer.Ordinal);
        var extension = new List<ToolRowViewModel>();
        var mcp = new List<ToolRowViewModel>();
        foreach (var descriptor in catalog)
        {
            // The first registration of a name is the one the host keeps (built-ins shadow external tools).
            if (!seen.Add(descriptor.Name))
            {
                continue;
            }

            var row = new ToolRowViewModel(descriptor, preferences);
            switch (descriptor.Origin)
            {
                case ToolOrigin.BuiltIn:
                    if (!builtIn.TryGetValue(descriptor.Group, out var list))
                    {
                        builtIn[descriptor.Group] = list = [];
                    }

                    list.Add(row);
                    break;
                case ToolOrigin.Extension:
                    extension.Add(row);
                    break;
                default:
                    mcp.Add(row);
                    break;
            }
        }

        var groups = new List<ToolGroupViewModel>();
        foreach (var id in BuiltInOrder)
        {
            if (builtIn.Remove(id, out var rows))
            {
                groups.Add(new ToolGroupViewModel(InstrumentsStrings.ToolsGroup(id), Sorted(rows)));
            }
        }

        foreach (var (id, rows) in builtIn.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            groups.Add(new ToolGroupViewModel(InstrumentsStrings.ToolsGroup(id), Sorted(rows)));
        }

        if (extension.Count > 0)
        {
            groups.Add(new ToolGroupViewModel(InstrumentsStrings.ToolsGroupExtensionTools, Sorted(extension)));
        }

        if (mcp.Count > 0)
        {
            groups.Add(new ToolGroupViewModel(InstrumentsStrings.ToolsGroupMcp, Sorted(mcp)));
        }

        return groups;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private static ToolRowViewModel[] Sorted(List<ToolRowViewModel> rows) =>
        [.. rows.OrderBy(static row => row.Name, StringComparer.Ordinal)];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a page whose window is gone (a test host).
        if (!disposed && Environment.CurrentManagedThreadId == uiThread)
        {
            Reload();
        }
    }
}
