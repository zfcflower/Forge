using System.Collections.ObjectModel;
using Forge.App.Composition;

namespace Forge.App.ViewModels;

/// <summary>
/// The settings surface. It is deliberately read-only: it shows where the settings file lives, what was read from it and
/// what the extension host reported, and it can reload. Nothing here edits a secret or invents a working model call.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private readonly AppComposition composition;
    private bool isOpen;
    private string providerDetail = string.Empty;

    public SettingsViewModel(AppComposition composition)
    {
        this.composition = composition;
        Paths = new ObservableCollection<SettingLine>();
        Rebuild();
    }

    public ObservableCollection<SettingLine> Paths { get; }

    public bool IsOpen
    {
        get => isOpen;
        set => Set(ref isOpen, value);
    }

    public string ProviderDetail
    {
        get => providerDetail;
        private set => Set(ref providerDetail, value);
    }

    public void Rebuild()
    {
        Paths.Clear();
        Paths.Add(new SettingLine("settings.json", composition.Paths.SettingsPath));
        Paths.Add(new SettingLine("数据目录", composition.Paths.UserDataDirectory));
        Paths.Add(new SettingLine("工作区", composition.WorkspaceDirectory));
        Paths.Add(new SettingLine("扩展宿主数据", composition.Paths.HostDataDirectory));
        Paths.Add(new SettingLine("插件", composition.Paths.PluginsDirectory));

        var provider = composition.Provider;
        ProviderDetail = provider.Provider is { } snapshot
            ? Strings.ProviderDetail(snapshot.Summary, snapshot.HasApiKey, snapshot.KeyFromEnvironment)
            : provider.Status;

        if (composition.HostProblem is { Length: > 0 } hostProblem)
        {
            Paths.Add(new SettingLine("扩展宿主", hostProblem));
        }

        if (composition.ApprovalsDatabasePath is { Length: > 0 } approvalsDatabase)
        {
            Paths.Add(new SettingLine("审批数据库", approvalsDatabase));
        }

        if (composition.Mcp is { } mcp)
        {
            Paths.Add(new SettingLine("MCP 服务器", Strings.McpDetail(mcp.ConfiguredServers, mcp.ConfigDiagnostics)));
        }

        if (composition.Editor is { } editor)
        {
            Paths.Add(new SettingLine("编辑器", Strings.EditorDetail(editor.LanguageCount, editor.GrammarCount, editor.Problem)));
        }
    }
}

/// <summary>One label/value row of the settings panel.</summary>
internal sealed record SettingLine(string Label, string Value);
