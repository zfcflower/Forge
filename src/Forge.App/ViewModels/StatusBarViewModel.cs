using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.HostClient;

namespace Forge.App.ViewModels;

/// <summary>
/// The status bar: extension host state, plugin count, provider state, MCP and workspace summary.
/// <para>Every value is a key or a count, never a resolved sentence: the text properties compute their text in the
/// current language, so a language switch only has to raise <c>PropertyChanged</c> (<see cref="RefreshText"/>). The
/// startup problems and the provider status are kept as <see cref="LocText"/> because they are computed before they are
/// shown; the exception message inside them is literal and is never translated.</para>
/// </summary>
internal sealed class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly IApprovalCounter? approvals;
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private string hostStateKey = ShellStrings.HostStartingKey;
    private LocText hostDetail;
    private LocText providerStatus;
    private int? mcpConfigured;
    private int? mcpReady;
    private int? approvalCount;
    private int? pluginCount;
    private int? toolCount;
    private string aheadBehindText = string.Empty;
    private string checksText = string.Empty;
    private bool isHostRunning;
    private bool isHostStarting = true;
    private bool isHostFailed;

    public StatusBarViewModel(AppComposition composition)
    {
        WorkspaceText = Shorten(composition.WorkspaceDirectory);
        ApplyProvider(composition.Provider);

        if (composition.Host is null)
        {
            SetHost(HostStatus.Stopped, composition.HostProblem);
        }

        if (composition.Mcp is { } mcp)
        {
            mcpConfigured = mcp.ConfiguredServers;
        }

        approvals = composition.ApprovalsCounter;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _ = RefreshApprovalsAsync();
    }

    /// <summary>Counts the approval records off the dispatcher; the status bar never waits on the database, and a
    /// database that cannot be read is shown as unavailable rather than as zero.</summary>
    public async Task RefreshApprovalsAsync()
    {
        if (approvals is null)
        {
            SetApprovals(null);
            return;
        }

        try
        {
            var counter = approvals;
            var records = await BackgroundRead.RunAsync(token => counter.CountAsync(token), CancellationToken.None)
                .ConfigureAwait(true);
            SetApprovals(records);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException)
        {
            SetApprovals(null);
        }
    }

    public string HostText => Loc.Instance[hostStateKey];

    /// <summary>Second line of the host message (the failure detail), empty when there is nothing to add.</summary>
    public string HostDetail => hostDetail.Resolve();

    public bool HasHostDetail => !hostDetail.IsEmpty;

    public string PluginsText => pluginCount is { } plugins && toolCount is { } tools
        ? ShellStrings.Plugins(plugins, tools)
        : ShellStrings.PluginsUnknown;

    public string ProviderText => providerStatus.IsEmpty
        ? ShellStrings.ProviderUnknown
        : ShellStrings.ProviderText(providerStatus);

    public string McpText => mcpConfigured is { } configured
        ? mcpReady is { } ready ? ShellStrings.McpState(configured, ready) : ShellStrings.Mcp(configured)
        : ShellStrings.McpNone;

    public string ApprovalsText => approvalCount is { } count
        ? ShellStrings.Approvals(count)
        : ShellStrings.ApprovalsUnknown;

    public string WorkspaceText { get; }

    /// <summary>
    /// Ahead/behind of the active session's branch, e.g. "↑2 ↓0"; empty when there is no upstream or no repository. The
    /// Git panel pushes it here whenever it re-reads the branch (a turn end and every git operation refresh it).
    /// </summary>
    public string AheadBehindText
    {
        get => aheadBehindText;
        private set
        {
            if (Set(ref aheadBehindText, value))
            {
                OnPropertyChanged(nameof(HasAheadBehind));
            }
        }
    }

    public bool HasAheadBehind => aheadBehindText.Length > 0;

    /// <summary>
    /// The CI marker of the active session's pull request ("CI ✓", "CI ✗", "CI …"), shown next to ahead/behind; empty when
    /// no wait for checks is running or has reported for that session's worktree. The Git panel pushes it here.
    /// </summary>
    public string ChecksText
    {
        get => checksText;
        private set
        {
            if (Set(ref checksText, value))
            {
                OnPropertyChanged(nameof(HasChecks));
            }
        }
    }

    public bool HasChecks => checksText.Length > 0;

    public bool IsHostRunning
    {
        get => isHostRunning;
        private set => Set(ref isHostRunning, value);
    }

    public bool IsHostStarting
    {
        get => isHostStarting;
        private set => Set(ref isHostStarting, value);
    }

    public bool IsHostFailed
    {
        get => isHostFailed;
        private set => Set(ref isHostFailed, value);
    }

    public void SetHost(HostStatus status, LocText? detail)
    {
        IsHostRunning = status == HostStatus.Running;
        IsHostStarting = status == HostStatus.Starting;
        IsHostFailed = status == HostStatus.Failed;
        hostStateKey = status switch
        {
            HostStatus.Running => ShellStrings.HostRunningKey,
            HostStatus.Starting => ShellStrings.HostStartingKey,
            HostStatus.Failed => ShellStrings.HostFailedKey,
            _ => ShellStrings.HostStoppedKey,
        };

        hostDetail = detail is { IsEmpty: false } text && !string.IsNullOrWhiteSpace(text.Resolve()) ? text : default;
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(HostDetail));
        OnPropertyChanged(nameof(HasHostDetail));

        if (status != HostStatus.Running)
        {
            pluginCount = null;
            toolCount = null;
            OnPropertyChanged(nameof(PluginsText));
        }
    }

    public void SetInventory(PluginInventory? inventory)
    {
        pluginCount = inventory?.PluginCount;
        toolCount = inventory?.ToolCount;
        OnPropertyChanged(nameof(PluginsText));
    }

    public void SetHostProcess(int? processId)
    {
        if (processId is { } id && IsHostRunning)
        {
            SetDetail(new LocText(ShellStrings.ProcessIdKey, TextFormat.Id(id)));
        }
    }

    public void ApplyProvider(ProviderSettings settings)
    {
        providerStatus = settings.StatusText;
        OnPropertyChanged(nameof(ProviderText));
    }

    /// <summary>Surfaces a storage or git problem in the status bar without pretending the app is fine.</summary>
    public void SetWorkspaceProblem(LocText problem) => SetDetail(problem);

    public void SetMcp(int configured, int ready)
    {
        mcpConfigured = configured;
        mcpReady = ready;
        OnPropertyChanged(nameof(McpText));
    }

    /// <summary>Called by the shell with the Git panel's "↑2 ↓0" (or an empty string when there is nothing to show).</summary>
    public void SetAheadBehind(string? text) => AheadBehindText = text ?? string.Empty;

    /// <summary>Called by the shell with the Git panel's CI marker (or an empty string when there is nothing to show).</summary>
    public void SetChecks(string? text) => ChecksText = text ?? string.Empty;

    public void SetApprovals(int? records)
    {
        approvalCount = records;
        OnPropertyChanged(nameof(ApprovalsText));
    }

    /// <summary>Re-reads every text that comes from <see cref="Loc"/>; an old value is never kept across a switch.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(HostDetail));
        OnPropertyChanged(nameof(HasHostDetail));
        OnPropertyChanged(nameof(PluginsText));
        OnPropertyChanged(nameof(ProviderText));
        OnPropertyChanged(nameof(McpText));
        OnPropertyChanged(nameof(ApprovalsText));
    }

    /// <summary>Drops the language subscription; the window view model disposes the status bar with itself.</summary>
    public void Dispose() => Loc.Instance.LanguageChanged -= OnLanguageChanged;

    private void SetDetail(LocText detail)
    {
        hostDetail = detail;
        OnPropertyChanged(nameof(HostDetail));
        OnPropertyChanged(nameof(HasHostDetail));
    }

    /// <summary>A notification from another thread belongs to a status bar whose window is gone (a test host).</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Environment.CurrentManagedThreadId == uiThread)
        {
            RefreshText();
        }
    }

    private static string Shorten(string path) =>
        path.Length <= 48 ? path : "…" + path[^47..];
}
