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
/// The <c>tools</c> section: the global tool permission mode on top, then the <c>run_code</c> script block (wave W14), the
/// context limits, the terminal block (wave W17a) and the product's tool catalog grouped by origin. Built-in groups come
/// first, in a fixed order, each tool with a switch unless another feature needs it; extension and MCP tools follow, read-only,
/// because their plugin or server is where they are switched. The catalog is a property of the installation
/// (<see cref="AppComposition.ToolInventory"/>), not of a session: the page lists and switches tools with no tab open and
/// before any turn has run, and what a particular session registered is the resource window's business. Switches write
/// <see cref="AppPreferences.ToolsDisabled"/>; every session applies it before its next turn. The permission radios write
/// <see cref="AppPreferences.ToolsPermission"/>, which each approval evaluation reads live. The script block edits
/// <see cref="AppPreferences.CodeScriptMaxConcurrentCalls"/>, <see cref="AppPreferences.CodeScriptResultChars"/>,
/// <see cref="AppPreferences.CodeScriptInnerResultChars"/> and <see cref="AppPreferences.CodeScriptTools"/> live, and the
/// terminal block edits the five terminal preferences (font family, font size, shell, arguments, scrollback) live, so a
/// change applies to the next terminal without a restart.
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
    private bool disposed;

    public ToolsSettingsSectionViewModel(AppComposition composition)
    {
        this.composition = composition;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Hint => InstrumentsStrings.ToolsSectionHint;

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    /// <summary>What the extension host registered for the whole app (plugins, plugin tools, commands).</summary>
    public string InventoryText
    {
        get => inventoryText;
        private set => SetAndNotify(ref inventoryText, value, nameof(HasInventory));
    }

    public bool HasInventory => inventoryText.Length > 0;

    // ---------------------------------------------------------------- the script (run_code) block, wave W14

    /// <summary>Smallest concurrency the field offers; it is the range the preference setter clamps to.</summary>
    public int MinCodeScriptMaxConcurrentCalls => AppPreferences.MinCodeScriptMaxConcurrentCalls;

    /// <summary>Largest concurrency the field offers.</summary>
    public int MaxCodeScriptMaxConcurrentCalls => AppPreferences.MaxCodeScriptMaxConcurrentCalls;

    /// <summary>Most inner tool calls one <c>run_code</c> script keeps in flight (1 to 32). Re-read after writing, so the field shows the clamped value.</summary>
    public int CodeScriptMaxConcurrentCalls
    {
        get => composition.Preferences.CodeScriptMaxConcurrentCalls;
        set
        {
            composition.Preferences.CodeScriptMaxConcurrentCalls = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Smallest result length the field offers.</summary>
    public int MinCodeScriptResultChars => AppPreferences.MinCodeScriptResultChars;

    /// <summary>Largest result length the field offers.</summary>
    public int MaxCodeScriptResultChars => AppPreferences.MaxCodeScriptResultChars;

    /// <summary>Characters of the result <c>run_code</c> returns to the model (1000 to 400000), re-read after writing.</summary>
    public int CodeScriptResultChars
    {
        get => composition.Preferences.CodeScriptResultChars;
        set
        {
            composition.Preferences.CodeScriptResultChars = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Smallest per-call result length the field offers.</summary>
    public int MinCodeScriptInnerResultChars => AppPreferences.MinCodeScriptInnerResultChars;

    /// <summary>Largest per-call result length the field offers.</summary>
    public int MaxCodeScriptInnerResultChars => AppPreferences.MaxCodeScriptInnerResultChars;

    /// <summary>Characters of one <c>Tools.Call</c> result inside a script (500 to 400000), re-read after writing.</summary>
    public int CodeScriptInnerResultChars
    {
        get => composition.Preferences.CodeScriptInnerResultChars;
        set
        {
            composition.Preferences.CodeScriptInnerResultChars = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Tools a script may call (comma, semicolon or newline separated, a trailing <c>*</c> as a wildcard); blank means every candidate.</summary>
    public string CodeScriptTools
    {
        get => composition.Preferences.CodeScriptTools;
        set
        {
            composition.Preferences.CodeScriptTools = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- context footprint: what one tool result may put into the conversation

    public string LimitsTitle => ToolsLimitsStrings.Section;

    public string BashCaptureLabel => ToolsLimitsStrings.BashCapture;

    public string BashCaptureHint => ToolsLimitsStrings.BashCaptureHint;

    public int MinBashCaptureKilobytes => AppPreferences.MinToolsBashCaptureKilobytes;

    public int MaxBashCaptureKilobytes => AppPreferences.MaxToolsBashCaptureKilobytes;

    /// <summary>Kilobytes of one command's output kept (16 to 4096); re-read after writing, so the field shows the clamped value.</summary>
    public int BashCaptureKilobytes
    {
        get => composition.Preferences.ToolsBashCaptureKilobytes;
        set
        {
            composition.Preferences.ToolsBashCaptureKilobytes = value;
            OnPropertyChanged();
        }
    }

    public string GrepResultsLabel => ToolsLimitsStrings.GrepResults;

    public string GrepResultsHint => ToolsLimitsStrings.GrepResultsHint;

    public string IgnoredDirectoriesLabel => ToolsLimitsStrings.IgnoredDirectories;

    public string IgnoredDirectoriesHint => ToolsLimitsStrings.IgnoredDirectoriesHint;

    /// <summary>Directory names a search skips below the path it was given (comma separated); read back from the preference.</summary>
    public string IgnoredDirectories
    {
        get => composition.Preferences.ToolsIgnoredDirectories;
        set
        {
            composition.Preferences.ToolsIgnoredDirectories = value;
            OnPropertyChanged();
        }
    }

    public int MinGrepResults => AppPreferences.MinToolsGrepMaxResults;

    public int MaxGrepResults => AppPreferences.MaxToolsGrepMaxResults;

    /// <summary>Matching lines one search returns (1 to 10000).</summary>
    public int GrepMaxResults
    {
        get => composition.Preferences.ToolsGrepMaxResults;
        set
        {
            composition.Preferences.ToolsGrepMaxResults = value;
            OnPropertyChanged();
        }
    }

    public string SpillLabel => ToolsLimitsStrings.Spill;
    public string SpillHint => ToolsLimitsStrings.SpillHint;
    public int MinSpillChars => AppPreferences.MinChatArtifactSpillChars;

    public int MaxSpillChars => AppPreferences.MaxChatArtifactSpillChars;

    /// <summary>Characters of a tool result kept inline before the rest is stored as an artifact (4000 to 256000).</summary>
    public int SpillChars
    {
        get => composition.Preferences.ChatArtifactSpillChars;
        set
        {
            composition.Preferences.ChatArtifactSpillChars = value;
            OnPropertyChanged();
        }
    }

    public string PreviewLabel => ToolsLimitsStrings.Preview;

    public string PreviewHint => ToolsLimitsStrings.PreviewHint;

    public int MinPreviewChars => AppPreferences.MinChatArtifactPreviewChars;

    public int MaxPreviewChars => AppPreferences.MaxChatArtifactPreviewChars;

    /// <summary>Characters of a spilled result that stay inline (500 to 100000; the host caps it at half the threshold).</summary>
    public int PreviewChars
    {
        get => composition.Preferences.ChatArtifactPreviewChars;
        set
        {
            composition.Preferences.ChatArtifactPreviewChars = value;
            OnPropertyChanged();
        }
    }

    public void Reload()
    {
        InventoryText = composition.Inventory is { } inventory
            ? InstrumentsStrings.ToolsInventory(inventory.PluginCount, inventory.ToolCount, inventory.CommandCount)
            : string.Empty;

        Groups.Clear();
        foreach (var group in Build(composition.ToolInventory(), composition.Preferences))
        {
            Groups.Add(group);
        }

        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(PermissionHeading));
        OnPropertyChanged(nameof(PermissionWorkspaceLabel));
        OnPropertyChanged(nameof(PermissionAllLabel));
        OnPropertyChanged(nameof(PermissionHint));
        OnPropertyChanged(nameof(PermissionWorkspace));
        OnPropertyChanged(nameof(PermissionAll));

        // The script and terminal blocks read the live preferences, so a settings.json change picked up by the reload
        // re-fills the fields.
        OnPropertyChanged(nameof(CodeScriptMaxConcurrentCalls));
        OnPropertyChanged(nameof(CodeScriptResultChars));
        OnPropertyChanged(nameof(CodeScriptInnerResultChars));
        OnPropertyChanged(nameof(CodeScriptTools));
        OnPropertyChanged(nameof(BashCaptureKilobytes));
        OnPropertyChanged(nameof(GrepMaxResults));
        OnPropertyChanged(nameof(IgnoredDirectories));
        OnPropertyChanged(nameof(SpillChars));
        OnPropertyChanged(nameof(PreviewChars));
        OnPropertyChanged(nameof(TerminalFontFamily));
        OnPropertyChanged(nameof(TerminalFontSize));
        OnPropertyChanged(nameof(TerminalShell));
        OnPropertyChanged(nameof(TerminalArguments));
        OnPropertyChanged(nameof(TerminalScrollbackLines));
    }

    // ---------------------------------------------------------------- the terminal block, wave W17a

    /// <summary>Font family of the terminal; empty follows the code font of the appearance section.</summary>
    public string TerminalFontFamily
    {
        get => composition.Preferences.TerminalFontFamily;
        set
        {
            composition.Preferences.TerminalFontFamily = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Largest font size the field offers. The field starts at 0 ("follow the code font") and the preference setter clamps
    /// everything between 1 and 7 up to the smallest real size, so the range it offers is 0 or 8-32.
    /// </summary>
    public decimal MaxTerminalFontSize => (decimal)AppPreferences.MaxTerminalFontSize;

    /// <summary>
    /// Font size of the terminal: 0 follows the code font, anything else is clamped to [8, 32] by the preference setter.
    /// The field re-reads the stored value, so it shows the clamped number rather than what was typed.
    /// </summary>
    public decimal TerminalFontSize
    {
        get => (decimal)composition.Preferences.TerminalFontSize;
        set
        {
            composition.Preferences.TerminalFontSize = (double)value;
            OnPropertyChanged();
        }
    }

    /// <summary>Shell executable of a new terminal; empty means the platform default.</summary>
    public string TerminalShell
    {
        get => composition.Preferences.TerminalShell;
        set
        {
            composition.Preferences.TerminalShell = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Arguments of the shell, space separated with double quotes for grouping.</summary>
    public string TerminalArguments
    {
        get => composition.Preferences.TerminalArguments;
        set
        {
            composition.Preferences.TerminalArguments = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Smallest scrollback the field offers; it is the range the preference setter clamps to.</summary>
    public decimal MinTerminalScrollbackLines => AppPreferences.MinScrollbackLines;

    /// <summary>Largest scrollback the field offers.</summary>
    public decimal MaxTerminalScrollbackLines => AppPreferences.MaxScrollbackLines;

    /// <summary>Scrollback lines a terminal keeps (the preference setter clamps it to 1000–100000).</summary>
    public decimal TerminalScrollbackLines
    {
        get => composition.Preferences.TerminalScrollbackLines;
        set
        {
            composition.Preferences.TerminalScrollbackLines = (int)Math.Clamp(
                Math.Round(value, MidpointRounding.AwayFromZero),
                AppPreferences.MinScrollbackLines,
                AppPreferences.MaxScrollbackLines);
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- tool permission: the global mode behind every approval

    /// <summary>Heading of the permission block at the top of the page.</summary>
    public string PermissionHeading => InstrumentsStrings.ToolsPermissionHeading;

    public string PermissionWorkspaceLabel => InstrumentsStrings.ToolsPermissionWorkspace;

    public string PermissionAllLabel => InstrumentsStrings.ToolsPermissionAll;

    public string PermissionHint => InstrumentsStrings.ToolsPermissionHint;

    /// <summary>
    /// "Workspace authorization" (the default): selected while the stored token is not
    /// <see cref="AppPreferences.ToolsPermissionAll"/>. Unchecking is ignored - the radio group always keeps one option chosen -
    /// so only the check that turns it on writes.
    /// </summary>
    public bool PermissionWorkspace
    {
        get => composition.Preferences.ToolsPermission != AppPreferences.ToolsPermissionAll;
        set
        {
            if (value)
            {
                SetPermission(AppPreferences.ToolsPermissionWorkspace);
            }
        }
    }

    /// <summary>"Authorize everything": no tool call asks. Same one-way rule as <see cref="PermissionWorkspace"/>.</summary>
    public bool PermissionAll
    {
        get => composition.Preferences.ToolsPermission == AppPreferences.ToolsPermissionAll;
        set
        {
            if (value)
            {
                SetPermission(AppPreferences.ToolsPermissionAll);
            }
        }
    }

    /// <summary>Writes the token and repaints both radios; every session reads it on its next approval evaluation.</summary>
    private void SetPermission(string token)
    {
        if (composition.Preferences.ToolsPermission == token)
        {
            return;
        }

        composition.Preferences.ToolsPermission = token;
        OnPropertyChanged(nameof(PermissionWorkspace));
        OnPropertyChanged(nameof(PermissionAll));
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
        // A notification from another thread belongs to a page whose window is gone.
        if (!disposed && Environment.CurrentManagedThreadId == uiThread)
        {
            Reload();
        }
    }
}
