using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.HostClient;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// Shared builders and probes for the agent-authored extension suites
/// (<c>extensions.stage / pending / commit / discard</c> and
/// <c>plugin.disable / enable / delete</c>).
/// Every helper is side-effect free except where it says otherwise; tests own their directories.
/// </summary>
internal static class ExtensionAuthoringSupport
{
    public const string ValidInputSchema = """{"type":"object"}""";

    public static Dictionary<string, string> PluginFiles(
        string id,
        string version = "1.0.0",
        string echoPrefix = "x") =>
        new(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Standard(id, echoPrefix, version),
        };

    public static Dictionary<string, string> WorkflowFiles(string workflowJson) =>
        new(StringComparer.Ordinal)
        {
            ["workflow.json"] = workflowJson,
        };

    public static Sdk.ExtensionToolSpec WorkflowTool(
        string name,
        string inputSchema = ValidInputSchema) =>
        new(name, "workflow tool " + name, inputSchema);

    public static Sdk.ExtensionStageRequest StagePluginRequest(
        string id,
        string version = "1.0.0",
        Dictionary<string, string>? files = null,
        IReadOnlyList<Sdk.Permission>? permissions = null,
        IReadOnlyList<Sdk.ExtensionDependency>? dependencies = null) =>
        new(
            id,
            version,
            Sdk.ExtensionKinds.Plugin,
            files ?? PluginFiles(id, version),
            permissions ?? [],
            dependencies ?? []);

    public static Sdk.ExtensionStageRequest StageWorkflowRequest(
        string id,
        string workflowJson,
        string version = "1.0.0",
        string toolName = "run-it",
        string inputSchema = ValidInputSchema,
        IReadOnlyList<Sdk.Permission>? permissions = null,
        IReadOnlyList<Sdk.ExtensionDependency>? dependencies = null) =>
        new(
            id,
            version,
            Sdk.ExtensionKinds.Workflow,
            WorkflowFiles(workflowJson),
            permissions ?? [],
            dependencies ?? [],
            WorkflowTool(toolName, inputSchema));

    /// <summary>A one-step workflow that calls <paramref name="tool"/> with <paramref name="argsJson"/>.</summary>
    public static string SingleStepWorkflow(string tool, string argsJson = "{}") =>
        $$"""{"steps":[{"id":"only","tool":"{{tool}}","after":[],"args":{{argsJson}}}]""" + "}";

    /// <summary>Two tool calls and their <c>host.tool_call</c> names as seen by the main program.</summary>
    public sealed record CapturedToolCall(string Name, string ArgumentsJson);

    /// <summary>
    /// An <see cref="ITool"/> the main program exposes to workflow steps. It records every call in arrival
    /// order and answers from <paramref name="answer"/>; <paramref name="entered"/> fires on entry so tests
    /// can order revoke/cancel against a running step without sleeping.
    /// </summary>
    public sealed class ProbeTool(
        string name,
        Func<CapturedToolCall, CancellationToken, Task<ToolResult>> answer,
        Action? entered = null) : ITool
    {
        private int invocations;

        public readonly List<CapturedToolCall> Calls = [];

        public ToolDefinition Definition { get; } = new(name, "probe tool " + name, """{"type":"object"}""");

        public ToolSideEffect SideEffect => ToolSideEffect.Write;

        public int Invocations => Volatile.Read(ref invocations);

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref invocations);
            var captured = new CapturedToolCall(call.Name, call.ArgumentsJson);
            lock (Calls)
            {
                Calls.Add(captured);
            }

            entered?.Invoke();
            return new ValueTask<ToolResult>(answer(captured, ct));
        }
    }

    /// <summary>Asserts the action fails with the host's business error <paramref name="code"/>.</summary>
    public static async Task<IpcRemoteException> AssertHostErrorAsync(
        Func<Task> action,
        string code,
        string? messageContains = null)
    {
        var error = await Assert.ThrowsAsync<IpcRemoteException>(action);
        Assert.Equal(code, error.Code);
        if (messageContains is not null)
        {
            Assert.Contains(messageContains, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        return error;
    }

    /// <summary>Snapshots every file under <paramref name="root"/> as relative path to SHA-256, except the host
    /// compiler cache: preparing a draft compiles it into its own <c>bin</c> directory, so a failed commit
    /// legitimately leaves new cache artifacts behind while changing nothing the approval covers.</summary>
    public static Dictionary<string, string> SnapshotTree(string root, bool includeCompilerCache = false)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
        {
            return snapshot;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!includeCompilerCache && IsCompilerCachePath(relative))
            {
                continue;
            }
            snapshot[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    public static void AssertSameTree(
        Dictionary<string, string> before,
        Dictionary<string, string> after,
        string what)
    {
        var missing = before.Keys.Except(after.Keys, StringComparer.Ordinal).ToArray();
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).ToArray();
        var changed = before.Keys
            .Intersect(after.Keys, StringComparer.Ordinal)
            .Where(key => !string.Equals(before[key], after[key], StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            missing.Length == 0 && added.Length == 0 && changed.Length == 0,
            $"{what} changed on disk: missing=[{string.Join(",", missing)}] " +
            $"added=[{string.Join(",", added)}] changed=[{string.Join(",", changed)}]");
    }

    private static bool IsCompilerCachePath(string relative) =>
        relative.Split('/').Contains("bin", StringComparer.Ordinal);

    public static string PendingDirectory(string pluginsDirectory, string id) =>
        Path.Combine(pluginsDirectory, ".pending", id);

    public static string LiveDirectory(string pluginsDirectory, string id) =>
        Path.Combine(pluginsDirectory, id);

    public static string TrashRoot(string pluginsDirectory) =>
        Path.Combine(pluginsDirectory, ".trash");

    public static string[] TrashEntries(string pluginsDirectory) =>
        Directory.Exists(TrashRoot(pluginsDirectory))
            ? Directory.GetDirectories(TrashRoot(pluginsDirectory))
            : [];

    /// <summary>Reads one staged file back from the host's pending area.</summary>
    public static string ReadPendingFile(string pluginsDirectory, string id, string relativePath) =>
        File.ReadAllText(
            Path.Combine(PendingDirectory(pluginsDirectory, id), relativePath.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>
/// A host bound to caller-owned temp directories that survives across client generations: stopping the
/// client keeps the plugins/data directories, and <see cref="RestartAsync"/> starts a fresh host process on
/// the same directories with a fresh pipe. Disposing stops the client and deletes the root.
/// </summary>
internal sealed class RestartableHost : IAsyncDisposable
{
    private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(180));
    private readonly List<string> pipes = [];
    private ExtensionHostClient? client;

    private RestartableHost(
        string root,
        Func<string, ITool?> toolResolver,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approvalPath,
        IReadOnlyList<string> hostArguments)
    {
        Root = root;
        PluginsDirectory = Path.Combine(root, "plugins");
        DataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(DataDirectory);
        ToolResolver = toolResolver;
        ApprovalPath = approvalPath;
        HostArguments = hostArguments;
    }

    public string Root { get; }

    public string PluginsDirectory { get; }

    public string DataDirectory { get; }

    public Func<string, ITool?> ToolResolver { get; }

    public Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> ApprovalPath { get; }

    public IReadOnlyList<string> HostArguments { get; }

    public ExtensionHostClient Client => client ?? throw new InvalidOperationException("The host is not running.");

    public CancellationToken Ct => budget.Token;

    public static RestartableHost Create(
        Func<string, ITool?>? toolResolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approvalPath = null,
        IReadOnlyList<string>? hostArguments = null) =>
        new(
            Path.Combine(Path.GetTempPath(), "forge-ext-" + Guid.NewGuid().ToString("N")[..12]),
            toolResolver ?? (_ => null),
            approvalPath ?? ((_, _) => ValueTask.FromResult(ApprovalDecision.Allow)),
            hostArguments ?? []);

    public async Task<ExtensionHostClient> StartAsync()
    {
        var pipe = "forge-ext-" + Guid.NewGuid().ToString("N")[..16];
        pipes.Add(pipe);
        var connection = new HostConnection
        {
            PipeName = pipe,
            HostExecutablePath = E2eFixture.HostAssemblyPath,
            RuntimePath = "dotnet",
            HostArguments = HostArguments,
            StartTimeout = TimeSpan.FromSeconds(60),
        };
        var options = new ExtensionHostClientOptions
        {
            PluginsDirectory = PluginsDirectory,
            DataDirectory = DataDirectory,
            RequestTimeout = TimeSpan.FromSeconds(60),
            StopRequestTimeout = TimeSpan.FromSeconds(5),
            ProcessExitTimeout = TimeSpan.FromSeconds(10),
        };
        client = new ExtensionHostClient(connection, ToolResolver, ApprovalPath, options);
        await client.StartAsync(Ct);
        await client.RefreshPluginsAsync(Ct);
        return client;
    }

    /// <summary>Stops the current host process but keeps every directory, then starts a fresh host on them.</summary>
    public async Task<ExtensionHostClient> RestartAsync()
    {
        if (client is not null)
        {
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await client.StopAsync(stopBudget.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
            {
            }

            await client.DisposeAsync();
            client = null;
        }

        return await StartAsync();
    }

    public async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(limit ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after 30 s waiting for {what}.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            try
            {
                using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.StopAsync(stopBudget.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException or IOException)
            {
            }

            await client.DisposeAsync();
            client = null;
        }

        budget.Dispose();
        for (var attempt = 0; attempt < 50 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }
}
