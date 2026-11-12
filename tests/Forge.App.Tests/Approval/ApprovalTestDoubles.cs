using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Tools;
using Forge.HostClient;
using Forge.Plugin.Sdk;
using ToolSideEffect = Forge.Abstractions.ToolSideEffect;

namespace Forge.App.Tests.Approval;

/// <summary>Shared doubles and builders for the approval suite. Every test writes rules to a temp directory.</summary>
internal sealed class InlineDispatcher : IApprovalDispatcher
{
    public static InlineDispatcher Instance { get; } = new();

    // Serializes like the UI thread would: the real dispatcher runs every queued item on one thread.
    private readonly Lock gate = new();

    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action)
    {
        lock (gate)
        {
            action();
        }

        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        lock (gate)
        {
            return Task.FromResult(func());
        }
    }

    public void Post(Action action)
    {
        lock (gate)
        {
            action();
        }
    }
}

internal sealed class FakeAuthoringClient : IExtensionAuthoringClient
{
    public List<ExtensionStageRequest> StageCalls { get; } = [];

    public List<string> DiscardCalls { get; } = [];

    public List<ExtensionCommitRequest> CommitCalls { get; } = [];

    public List<(string Id, string Version)> DisableCalls { get; } = [];

    public List<(string Id, string Version)> EnableCalls { get; } = [];

    public List<(string Id, string Version)> DeleteCalls { get; } = [];

    public IReadOnlyList<StagedExtension> Pending { get; set; } = [];

    public Func<ExtensionStageRequest, StagedExtension>? StageHandler { get; set; }

    public Func<ExtensionCommitRequest, ExtensionCommitResult>? CommitHandler { get; set; }

    public Exception? StageError { get; set; }

    public Exception? CommitError { get; set; }

    public Exception? DiscardError { get; set; }

    public Task<StagedExtension> StageExtensionAsync(ExtensionStageRequest request, CancellationToken ct)
    {
        StageCalls.Add(request);
        if (StageError is not null)
        {
            return Task.FromException<StagedExtension>(StageError);
        }

        return Task.FromResult(StageHandler?.Invoke(request) ?? ApprovalFixtures.Staged(request.PluginId, request.Version));
    }

    public Task<IReadOnlyList<StagedExtension>> GetPendingExtensionsAsync(CancellationToken ct) =>
        Task.FromResult(Pending);

    public Task<ExtensionCommitResult> CommitExtensionAsync(ExtensionCommitRequest request, CancellationToken ct)
    {
        CommitCalls.Add(request);
        if (CommitError is not null)
        {
            return Task.FromException<ExtensionCommitResult>(CommitError);
        }

        return Task.FromResult(
            CommitHandler?.Invoke(request)
            ?? new ExtensionCommitResult(
                new PluginStatusInfo(request.PluginId, "1.0.0", "Approved", request.SourceHash, request.DependencyHash, "sdk", []),
                []));
    }

    public Task DiscardExtensionAsync(string pluginId, CancellationToken ct)
    {
        DiscardCalls.Add(pluginId);
        return DiscardError is not null ? Task.FromException(DiscardError) : Task.CompletedTask;
    }

    public Task DisablePluginAsync(string pluginId, string version, CancellationToken ct)
    {
        DisableCalls.Add((pluginId, version));
        return Task.CompletedTask;
    }

    public Task EnablePluginAsync(string pluginId, string version, CancellationToken ct)
    {
        EnableCalls.Add((pluginId, version));
        return Task.CompletedTask;
    }

    public Task DeletePluginAsync(string pluginId, string version, CancellationToken ct)
    {
        DeleteCalls.Add((pluginId, version));
        return Task.CompletedTask;
    }
}

internal sealed class FakeContributionsClient : IExtensionContributionClient
{
    public IReadOnlyList<PluginStatusInfo> Statuses { get; set; } = [];

    public Exception? StatusesError { get; set; }

    public HostStatus Status { get; set; } = HostStatus.Running;

    public IReadOnlyList<ITool> Tools => [];

    public IReadOnlyList<ICommand> Commands => [];

    public IReadOnlyList<WorkflowDefinition> Workflows => [];

    public IReadOnlyList<AgentDefinition> Agents => [];

    public IReadOnlyList<PluginPromptReference> PromptContributors => [];

#pragma warning disable CS0067 // Test double: the suite subscribes to Changed; the host-side events are never raised here.
    public event Action? ToolsChanged;
    public event Action<HostStatus, string?>? StatusChanged;
    public event Action? ContributionsChanged;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RefreshPluginsAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ApprovePluginAsync(string pluginId, string version, string sourceHash, string dependencyHash, string sdkVersion, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RevokePluginAsync(string pluginId, string version, CancellationToken ct) => Task.CompletedTask;

    public ITool CreateRunCodeTool(IToolRegistry tools, RunCodeProxyOptions? options = null) =>
        throw new NotSupportedException("The approval suite never builds a run_code proxy.");

    public Task<IReadOnlyList<PluginStatusInfo>> GetPluginStatusesAsync(CancellationToken ct) =>
        StatusesError is not null
            ? Task.FromException<IReadOnlyList<PluginStatusInfo>>(StatusesError)
            : Task.FromResult(Statuses);

    public Task<string?> GetPluginPromptAsync(PluginPromptReference contributor, ToolContext context, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeHostState : IExtensionHostState
{
    public bool HasHost { get; set; } = true;

    public string? HostProblem { get; set; }

    public HostStatus Status { get; set; } = HostStatus.Running;

    public string? StatusDetail { get; set; }

    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();
}

internal sealed class FakeAdmin : IExtensionsAdmin
{
    public ExtensionSnapshot Snapshot { get; set; } = new([], [], null);

    public Exception? ReadError { get; set; }

    public List<(string Id, string Version, bool Enabled)> SetEnabledCalls { get; } = [];

    public List<(string Id, string Version)> DeleteCalls { get; } = [];

    public Func<string, StagedExtension?>? PendingLookup { get; set; }

    public int ReadCount { get; private set; }

    public Task<ExtensionSnapshot> ReadAsync(CancellationToken ct)
    {
        ReadCount++;
        return ReadError is not null ? Task.FromException<ExtensionSnapshot>(ReadError) : Task.FromResult(Snapshot);
    }

    public Task<StagedExtension?> FindPendingAsync(string pluginId, CancellationToken ct) =>
        Task.FromResult(PendingLookup?.Invoke(pluginId));

    public Task SetEnabledAsync(string pluginId, string version, bool enabled, CancellationToken ct)
    {
        SetEnabledCalls.Add((pluginId, version, enabled));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string pluginId, string version, CancellationToken ct)
    {
        DeleteCalls.Add((pluginId, version));
        return Task.CompletedTask;
    }
}

internal static class ApprovalFixtures
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    public static string TempDir(string prefix) => TestTempFiles.NewDirectory(prefix);

    public static void DeleteTree(string path) => TestTempFiles.DeleteTree(path);

    public static SessionInfo Session(string id = "s1", string? projectId = "p1") =>
        new(id, Path.Combine(Path.GetTempPath(), "ws-" + id), "Session " + id, DateTimeOffset.UtcNow)
        {
            ProjectId = projectId,
        };

    public static ApprovalRequest ToolRequest(
        string tool = "shell_run",
        ToolSideEffect sideEffect = ToolSideEffect.Execute,
        string args = "{}") =>
        new(tool, args, sideEffect, Path.GetTempPath());

    public static StagedExtension Staged(
        string pluginId,
        string version = "1.0.0",
        string? liveVersion = null,
        IReadOnlyList<string>? diagnostics = null,
        string sourceHash = "sha-source",
        string dependencyHash = "sha-deps",
        string sdkVersion = "sdk-12") =>
        new(
            pluginId,
            version,
            ExtensionKinds.Plugin,
            [new StagedFile("main.cs", "new text", liveVersion is null ? null : "old text")],
            [new Permission(PermissionKind.ReadFile, "/tmp")],
            [],
            sourceHash,
            dependencyHash,
            sdkVersion,
            diagnostics ?? [],
            liveVersion);

    public static PluginStatusInfo Installed(
        string pluginId,
        string version = "1.0.0",
        string status = "Approved") =>
        new(pluginId, version, status, "sha-source", "sha-deps", "sdk-12", []);

    public static ExtensionDraft PluginDraft(
        string pluginId,
        bool isUpdate = false,
        string version = "1.0.0",
        string kind = "plugin") =>
        new(
            Id: pluginId,
            Version: version,
            Kind: kind,
            Files: new Dictionary<string, string> { ["main.cs"] = "class X {}" },
            Permissions: [new ExtensionPermissionDraft("ReadFile", "/tmp")],
            Dependencies: [],
            Tool: kind == ExtensionDraftKinds.Workflow
                ? new ExtensionToolDraft(pluginId, "does things", "{\"type\":\"object\"}")
                : null,
            IsUpdate: isUpdate);

    /// <summary>Bounded wait for fire-and-forget background work (argument formatting, diff building).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + StepTimeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Bounded wait over an asynchronous condition (queue counts, admin calls).</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + StepTimeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Captures the next tool card; subscribe before starting the request so nothing is missed.</summary>
    public static Task<ToolApprovalCardViewModel> NextToolCardAsync(ApprovalQueue queue)
    {
        var ready = new TaskCompletionSource<ToolApprovalCardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ApprovalCardViewModel card)
        {
            if (card is ToolApprovalCardViewModel tool)
            {
                queue.CardAdded -= Handler;
                ready.TrySetResult(tool);
            }
        }

        queue.CardAdded += Handler;
        return ready.Task;
    }

    /// <summary>Captures the next extension card; subscribe before starting the submission.</summary>
    public static Task<ExtensionApprovalCardViewModel> NextExtensionCardAsync(ApprovalQueue queue)
    {
        var ready = new TaskCompletionSource<ExtensionApprovalCardViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ApprovalCardViewModel card)
        {
            if (card is ExtensionApprovalCardViewModel extension)
            {
                queue.CardAdded -= Handler;
                ready.TrySetResult(extension);
            }
        }

        queue.CardAdded += Handler;
        return ready.Task;
    }
}
