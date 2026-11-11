using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Preferences;

namespace Forge.App.Services;

/// <summary>
/// Everything the composer's instrument strip reads that does not belong to one session: the app-wide counts it shows
/// next to the session's own ones, the model metadata chain, and the two ways to open the session's project folder.
/// <para>The shell implements it once per window; a test can fake it. The blocking members (<see cref="Skills"/>) are only
/// ever called from a pool thread, the async ones come back on the caller's context.</para>
/// </summary>
internal interface ISessionInstrumentSources
{
    /// <summary>Approval cards queued for one session right now. UI thread only (the queue belongs to it).</summary>
    int PendingApprovals(string sessionId);

    /// <summary>Records in the approvals database, or null when it cannot be read; runs off the UI thread.</summary>
    Task<int?> ApprovalRecordsAsync(CancellationToken ct);

    /// <summary>Servers that reported ready and servers configured, in that order.</summary>
    (int Ready, int Configured) McpServers();

    /// <summary>Skills discoverable for one workspace. Blocking: call it on a pool thread.</summary>
    IReadOnlyList<SlashEntry> Skills(string workspacePath);

    /// <summary>What the metadata chain (local table → provider → user override) knows about one model.</summary>
    /// <summary>What is known about the model the composer shows, in the context of the provider that serves it.</summary>
    ModelMetadata ModelMetadata(string modelId, string? providerId = null);

    /// <summary>True when a VS Code executable could be resolved, so the menu item is only offered when it works.</summary>
    bool HasVsCode { get; }

    /// <summary>Opens one folder in VS Code; a failure is traced, never thrown at the UI.</summary>
    void OpenInVsCode(string path);

    /// <summary>Reveals one folder in the platform file manager.</summary>
    void OpenInExplorer(string path);

    /// <summary>Opens the settings page on one section id (the counters' click target).</summary>
    void OpenSettingsSection(string sectionId);
}

/// <summary>The shell's implementation: composition services, the approval queue and the platform launchers.</summary>
internal sealed class SessionInstrumentSources : ISessionInstrumentSources
{
    private readonly AppComposition composition;
    private readonly Func<string, int> pendingApprovals;
    private readonly Action<string> openSettingsSection;
    private readonly Lazy<string?> vsCode = new(ResolveVsCode, LazyThreadSafetyMode.PublicationOnly);

    public SessionInstrumentSources(
        AppComposition composition,
        Func<string, int> pendingApprovals,
        Action<string> openSettingsSection)
    {
        ArgumentNullException.ThrowIfNull(composition);
        this.composition = composition;
        this.pendingApprovals = pendingApprovals ?? throw new ArgumentNullException(nameof(pendingApprovals));
        this.openSettingsSection = openSettingsSection ?? throw new ArgumentNullException(nameof(openSettingsSection));
    }

    public bool HasVsCode => vsCode.Value is { Length: > 0 };

    public int PendingApprovals(string sessionId) => pendingApprovals(sessionId);

    public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => composition.ApprovalsCounter.CountAsync(ct);

    public (int Ready, int Configured) McpServers() =>
        composition.Mcp is { } mcp ? (mcp.ReadyCount(), mcp.ConfiguredServers) : (0, 0);

    public IReadOnlyList<SlashEntry> Skills(string workspacePath) =>
        SkillCatalog.Discover(workspacePath, AgentRuntimeServices.UserDirectory());

    public ModelMetadata ModelMetadata(string modelId, string? providerId = null) =>
        composition.Providers.Catalog.MetadataFor(modelId, providerId);

    public void OpenInVsCode(string path)
    {
        if (vsCode.Value is not { Length: > 0 } executable || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TryStart(new ProcessStartInfo
        {
            FileName = executable,
            ArgumentList = { "-n", path },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public void OpenInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "explorer.exe", Arguments = "\"" + path + "\"", UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo { FileName = "open", Arguments = "\"" + path + "\"", UseShellExecute = true }
                : new ProcessStartInfo { FileName = "xdg-open", Arguments = "\"" + path + "\"", UseShellExecute = true };
        TryStart(start);
    }

    public void OpenSettingsSection(string sectionId)
    {
        if (!string.IsNullOrWhiteSpace(sectionId))
        {
            openSettingsSection(sectionId);
        }
    }

    private static void TryStart(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Trace.WriteLine("[instruments] launch failed: " + exception.Message);
        }
    }

    /// <summary>
    /// Finds the VS Code command-line shim. On Windows that is <c>code.cmd</c> in the install's <c>bin</c> or on PATH; on
    /// the other platforms the <c>code</c> script. Nothing is offered when it cannot be resolved.
    /// </summary>
    private static string? ResolveVsCode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "code.cmd", "code.exe" } : new[] { "code" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length == 0)
        {
            return null;
        }

        var installed = Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd");
        return File.Exists(installed) ? installed : null;
    }
}

/// <summary>
/// The null object: a session opened by a test or by a shell without the instrument wiring still has a strip, whose counters
/// read zero and whose buttons do nothing, instead of a broken binding.
/// </summary>
internal sealed class EmptySessionInstrumentSources : ISessionInstrumentSources
{
    public static EmptySessionInstrumentSources Instance { get; } = new();

    public bool HasVsCode => false;

    public int PendingApprovals(string sessionId) => 0;

    public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => Task.FromResult<int?>(0);

    public (int Ready, int Configured) McpServers() => (0, 0);

    public IReadOnlyList<SlashEntry> Skills(string workspacePath) => [];

    public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => Abstractions.ModelMetadata.Unknown;

    public void OpenInVsCode(string path)
    {
    }

    public void OpenInExplorer(string path)
    {
    }

    public void OpenSettingsSection(string sectionId)
    {
    }
}
