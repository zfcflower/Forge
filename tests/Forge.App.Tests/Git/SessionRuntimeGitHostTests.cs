using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.Core.Storage;
using Forge.HostClient;
using Forge.Ipc;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The git tools next to a real <see cref="ExtensionHostClient"/> that has registered tools (a loopback Extension Host
/// stand-in speaking the IPC protocol): <c>run_code</c> exists, its script registry holds the host tools only and never a
/// <c>git_*</c> tool, and after the host changes its tool set (<c>OnToolsChanged</c> ending in
/// <c>AgentHost.ReplaceExternalTools</c>) the git tools are still in the registry the session runs with.
/// <para>The runtime subscribes to the client's <c>ToolsChanged</c> when its agent host is built, so one scripted turn is
/// run first. The tool surface is read three ways: <see cref="SessionRuntime.BuildTools"/>, the live registry through
/// <see cref="ISessionToolScope.FindTool"/> and the tool definitions the provider received in its request.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionRuntimeGitHostTests
{
    private static readonly string[] GitToolNames =
    [
        GitTools.StatusName,
        GitTools.LogName,
        GitTools.DiffName,
        GitTools.FetchName,
        GitTools.BranchName,
        GitTools.SyncName,
    ];

    [Fact]
    public async Task A_host_with_tools_gets_run_code_with_no_git_tool_in_its_registry()
    {
        await using var rig = await HostRig.StartAsync();
        await rig.RegisterAsync("alpha", "echo");

        var tools = rig.Runtime.BuildTools();
        var names = tools.Select(static tool => tool.Definition.Name).ToList();

        // Host tools first, then run_code, then the git tools in registration order.
        Assert.Equal(["echo", "run_code", .. GitToolNames], names);

        var runCode = tools.Single(static tool => tool.Definition.Name == "run_code");
        var listing = runCode.Definition.Description;
        Assert.Contains("Tools (1), sorted by name:", listing, StringComparison.Ordinal);
        Assert.Contains("  echo [read-only]:", listing, StringComparison.Ordinal);
        foreach (var name in GitToolNames)
        {
            Assert.DoesNotContain(name, listing, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("git_", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_tools_stay_in_the_host_registry_across_tool_changes_and_run_code_never_lists_them()
    {
        await using var rig = await HostRig.StartAsync();
        await rig.RegisterAsync("alpha", "echo");

        // The first turn builds the agent host and subscribes the runtime to the client's ToolsChanged.
        await rig.RunTurnAsync();
        var first = Assert.Single(rig.Provider.Requests);
        Assert.Contains("echo", first);
        Assert.Contains("run_code", first);
        foreach (var name in GitToolNames)
        {
            Assert.Contains(name, first);
        }

        var scope = (ISessionToolScope)rig.Runtime;
        foreach (var name in GitToolNames)
        {
            Assert.NotNull(scope.FindTool(name));
        }

        // A second plugin registers: the loop's surface is replaced, the git tools survive, run_code lists both host tools.
        await rig.RegisterAndWaitAsync("beta", "echo2");
        Assert.NotNull(scope.FindTool("echo"));
        Assert.NotNull(scope.FindTool("echo2"));
        foreach (var name in GitToolNames)
        {
            Assert.NotNull(scope.FindTool(name));
        }

        var runCode = scope.FindTool("run_code");
        Assert.NotNull(runCode);
        Assert.Contains("Tools (2), sorted by name:", runCode!.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("  echo2 [read-only]:", runCode.Definition.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("git_", runCode.Definition.Description, StringComparison.Ordinal);

        // The first plugin goes away.
        await rig.UnregisterAndWaitAsync("alpha");
        Assert.Null(scope.FindTool("echo"));
        Assert.NotNull(scope.FindTool("echo2"));
        foreach (var name in GitToolNames)
        {
            Assert.NotNull(scope.FindTool(name));
        }

        Assert.Contains("Tools (1), sorted by name:", scope.FindTool("run_code")!.Definition.Description, StringComparison.Ordinal);

        // The last one goes away: no host tool, so no run_code - and the git tools are still there.
        await rig.UnregisterAndWaitAsync("beta");
        Assert.Null(scope.FindTool("echo2"));
        Assert.Null(scope.FindTool("run_code"));
        foreach (var name in GitToolNames)
        {
            Assert.NotNull(scope.FindTool(name));
        }

        // The model sees the same: its next request carries the git tools and no run_code.
        await rig.RunTurnAsync();
        var last = rig.Provider.Requests[^1];
        Assert.DoesNotContain("run_code", last);
        Assert.DoesNotContain("echo", last);
        Assert.DoesNotContain("echo2", last);
        foreach (var name in GitToolNames)
        {
            Assert.Contains(name, last);
        }
    }

    [Fact]
    public async Task A_tool_change_before_the_first_turn_is_picked_up_when_the_host_is_built()
    {
        await using var rig = await HostRig.StartAsync();

        // No turn yet, so no host and no subscription: the registration is simply read when the host is built.
        await rig.RegisterAsync("alpha", "echo");
        await rig.RegisterAsync("beta", "echo2");
        await rig.RunTurnAsync();

        var first = Assert.Single(rig.Provider.Requests);
        Assert.Contains("echo", first);
        Assert.Contains("echo2", first);
        Assert.Contains("run_code", first);
        foreach (var name in GitToolNames)
        {
            Assert.Contains(name, first);
        }
    }

    // ------------------------------------------------------------ rig

    private sealed class RecordingProvider : IChatProvider
    {
        private readonly List<IReadOnlyList<string>> requests = [];

        public string Id => "recording";

        public IReadOnlyList<IReadOnlyList<string>> Requests
        {
            get
            {
                lock (requests)
                {
                    return [.. requests];
                }
            }
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            lock (requests)
            {
                requests.Add([.. request.Tools.Select(static tool => tool.Name)]);
            }

            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new MessageCompleted(ChatMessage.Assistant("ok"), new TokenUsage(1, 1), StopReason.EndTurn);
        }
    }

    /// <summary>A pipe server speaking just enough of the Extension Host protocol: it answers every request with an empty object.</summary>
    private sealed class MiniExtensionHost : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private IpcPeer? peer;

        public string PipeName { get; } = "forge-git-host-" + Guid.NewGuid().ToString("N");

        public async Task AcceptAsync()
        {
            var server = await IpcPipe.CreateServer(PipeName, lifetime.Token);
            var created = new IpcPeer(server, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(15) });
            created.RequestReceived += (message, _) =>
                created.RespondAsync(message.Id, IpcJsonTypes.EmptyObject, CancellationToken.None);
            await created.StartAsync(CancellationToken.None);
            peer = created;
        }

        public Task NotifyAsync(string operation, string argsJson)
        {
            using var document = JsonDocument.Parse(argsJson);
            return (peer ?? throw new InvalidOperationException("no client connected")).SendNotificationAsync(
                operation,
                document.RootElement.Clone(),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            if (peer is not null)
            {
                await peer.DisposeAsync();
            }

            lifetime.Dispose();
        }
    }

    private sealed class HostRig : IAsyncDisposable
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
        private readonly string root;
        private readonly MiniExtensionHost host;
        private readonly SqliteSessionStore store;
        private readonly SqliteTodoStore todos;

        private HostRig(string root, MiniExtensionHost host, ExtensionHostClient client, SqliteSessionStore store, SqliteTodoStore todos, RecordingProvider provider, SessionRuntime runtime)
        {
            this.root = root;
            this.host = host;
            Client = client;
            this.store = store;
            this.todos = todos;
            Provider = provider;
            Runtime = runtime;
        }

        public ExtensionHostClient Client { get; }

        public RecordingProvider Provider { get; }

        public SessionRuntime Runtime { get; }

        public static async Task<HostRig> StartAsync()
        {
            var root = GitTestFiles.NewTempDir("forge-git-host-");
            var host = new MiniExtensionHost();
            ExtensionHostClient? client = null;
            SqliteSessionStore? store = null;
            SqliteTodoStore? todos = null;
            try
            {
                client = new ExtensionHostClient(new HostConnection { PipeName = host.PipeName, StartTimeout = TimeSpan.FromSeconds(10) });
                var accepting = host.AcceptAsync();
                using (var limit = new CancellationTokenSource(Budget))
                {
                    await client.StartAsync(limit.Token);
                }

                await accepting.WaitAsync(Budget);

                var dataDirectory = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
                var workDirectory = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
                store = new SqliteSessionStore(dataDirectory);
                todos = new SqliteTodoStore(dataDirectory);
                var info = await store.CreateSessionAsync(workDirectory, "git host", CancellationToken.None);

                var provider = new RecordingProvider();
                var services = new AgentRuntimeServices(
                    _ => provider,
                    client,
                    store,
                    store,
                    todos,
                    new ModelSettings("test"),
                    Git: new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()));
                var runtime = new SessionRuntime(services, info);
                return new HostRig(root, host, client, store, todos, provider, runtime);
            }
            catch
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                }

                await host.DisposeAsync();
                store?.Dispose();
                todos?.Dispose();
                GitTestFiles.DeleteTree(root);
                throw;
            }
        }

        /// <summary>Registers one read-only tool for <paramref name="pluginId"/> and waits until the client has processed it.</summary>
        public async Task RegisterAsync(string pluginId, string tool)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => seen.TrySetResult();
            Client.ToolsChanged += OnChanged;
            try
            {
                await host.NotifyAsync(
                    "tools.register",
                    $$"""{"pluginId":"{{pluginId}}","tools":[{"name":"{{tool}}","description":"d","inputSchemaJson":"{}","sideEffect":0}]}""");
                await seen.Task.WaitAsync(Budget);
            }
            finally
            {
                Client.ToolsChanged -= OnChanged;
            }
        }

        /// <summary>
        /// As <see cref="RegisterAsync"/>, for use after the runtime has subscribed: the waiter subscribes later than the
        /// runtime, and handlers of one event run in subscription order, so by the time it fires the runtime's has finished.
        /// </summary>
        public Task RegisterAndWaitAsync(string pluginId, string tool) => RegisterAsync(pluginId, tool);

        public async Task UnregisterAndWaitAsync(string pluginId)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => seen.TrySetResult();
            Client.ToolsChanged += OnChanged;
            try
            {
                await host.NotifyAsync("tools.unregister", $$"""{"pluginId":"{{pluginId}}"}""");
                await seen.Task.WaitAsync(Budget);
            }
            finally
            {
                Client.ToolsChanged -= OnChanged;
            }
        }

        public async Task RunTurnAsync()
        {
            using var limit = new CancellationTokenSource(Budget);
            var events = new List<AgentEvent>();
            await Runtime.RunTurnAsync("hello", events.Add, limit.Token);
            Assert.DoesNotContain(events, static e => e is AgentFailed);
        }

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            await Client.DisposeAsync();
            await host.DisposeAsync();
            store.Dispose();
            todos.Dispose();

            // Sqlite keeps the file handles until the pool is cleared.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            GitTestFiles.DeleteTree(root);
        }
    }
}
