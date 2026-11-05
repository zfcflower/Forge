using Forge.App.Composition;
using Forge.App.Services;
using Forge.HostClient;

namespace Forge.App.ViewModels;

/// <summary>The status bar: extension host state, plugin count, provider state, MCP and workspace summary.</summary>
internal sealed class StatusBarViewModel : ObservableObject
{
    private readonly IApprovalCounter? approvals;
    private string hostText = Strings.HostStarting;
    private string pluginsText = Strings.PluginsUnknown;
    private string providerText = Strings.ProviderUnknown;
    private string mcpText = Strings.McpNone;
    private string approvalsText = Strings.ApprovalsUnknown;
    private string hostDetail = string.Empty;
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
            mcpText = Strings.Mcp(mcp.ConfiguredServers);
        }

        approvals = composition.ApprovalsCounter;
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

    public string HostText
    {
        get => hostText;
        private set => Set(ref hostText, value);
    }

    /// <summary>Second line of the host message (the failure detail), empty when there is nothing to add.</summary>
    public string HostDetail
    {
        get => hostDetail;
        private set
        {
            if (Set(ref hostDetail, value))
            {
                OnPropertyChanged(nameof(HasHostDetail));
            }
        }
    }

    public bool HasHostDetail => hostDetail.Length > 0;

    public string PluginsText
    {
        get => pluginsText;
        private set => Set(ref pluginsText, value);
    }

    public string ProviderText
    {
        get => providerText;
        private set => Set(ref providerText, value);
    }

    public string McpText
    {
        get => mcpText;
        private set => Set(ref mcpText, value);
    }

    public string ApprovalsText
    {
        get => approvalsText;
        private set => Set(ref approvalsText, value);
    }

    public string WorkspaceText { get; }

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

    public void SetHost(HostStatus status, string? detail)
    {
        IsHostRunning = status == HostStatus.Running;
        IsHostStarting = status == HostStatus.Starting;
        IsHostFailed = status == HostStatus.Failed;
        HostText = status switch
        {
            HostStatus.Running => Strings.HostRunning,
            HostStatus.Starting => Strings.HostStarting,
            HostStatus.Failed => Strings.HostFailed,
            _ => Strings.HostStopped,
        };
        HostDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail;
        if (status != HostStatus.Running)
        {
            PluginsText = Strings.PluginsUnknown;
        }
    }

    public void SetInventory(PluginInventory? inventory)
    {
        PluginsText = inventory is null
            ? Strings.PluginsUnknown
            : Strings.Plugins(inventory.PluginCount, inventory.ToolCount);
    }

    public void SetHostProcess(int? processId)
    {
        if (processId is { } id && IsHostRunning)
        {
            HostDetail = Strings.ProcessId(id);
        }
    }

    public void ApplyProvider(ProviderSettings settings) => ProviderText = Strings.ProviderText(settings.Status);

    /// <summary>Surfaces a storage or git problem in the status bar without pretending the app is fine.</summary>
    public void SetWorkspaceProblem(string problem) => HostDetail = problem;

    public void SetMcp(int configured, int ready) => McpText = Strings.McpState(configured, ready);

    public void SetApprovals(int? records) => ApprovalsText = records is { } count
        ? Strings.Approvals(count)
        : Strings.ApprovalsUnknown;

    private static string Shorten(string path) =>
        path.Length <= 48 ? path : "…" + path[^47..];
}
