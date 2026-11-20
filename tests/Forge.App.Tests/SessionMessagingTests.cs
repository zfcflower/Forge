using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

public sealed class SessionMessagingTests
{
    private const string WorkDirectoryName = "work";
    private static readonly AgentDefinition UserAgent = new("Worker", "worker", "worker prompt", new ModelSettings("model"), []);
    private static readonly AgentDefinition ModelAgent = new("ModelWorker", "model worker", "model prompt", new ModelSettings("model"), ["read"]);

    private sealed class CountingProvider : IChatProvider
    {
        private int calls;

        public string Id => "unused";

        public int Calls => Volatile.Read(ref calls);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("model turn completed")]),
                TokenUsage.Empty,
                StopReason.EndTurn);
        }
    }

    private sealed class EventSink : IAgentEventSink
    {
        public void Emit(AgentEvent value) { }
    }

    private sealed class FakeEndpoint : ISessionMessageEndpoint
    {
        private readonly TaskCompletionSource<SessionTurnOutcome?> heldTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SessionTurnRequest> firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> firstReceipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<SessionTurnRequest> requests = new();
        private readonly ConcurrentQueue<string> receipts = new();

        public Func<string, Task>? OnReceipt { get; set; }

        public bool RootBusy { get; set; }

        public IReadOnlyList<SessionTurnRequest> Requests => requests.ToArray();

        public IReadOnlyList<string> Receipts => receipts.ToArray();

        public Task<SessionTurnRequest> FirstRequest => firstRequest.Task;

        public Task<string> FirstReceipt => firstReceipt.Task;

        public bool IsBusy(string sessionId) => RootBusy;

        public Task<SessionTurnOutcome?> RunSessionTurnAsync(SessionTurnRequest request, CancellationToken ct)
        {
            requests.Enqueue(request);
            firstRequest.TrySetResult(request);
            return heldTurn.Task;
        }

        public Task<int> BeginSessionTurnAsync(string sessionId, string workingDirectory, CancellationToken ct) => Task.FromResult(1);

        public Task EndSessionTurnAsync(string sessionId, int turn, SessionReplyStatus status, string? reason, string workingDirectory, CancellationToken ct) => Task.CompletedTask;

        public Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct) =>
            Task.FromResult<SessionTurnChanges?>(new SessionTurnChanges(2, 5, 3));

        public void DeliverReceipt(string initiatorSessionId, string text)
        {
            receipts.Enqueue(text);
            if (OnReceipt is { } callback)
            {
                _ = callback(text);
            }

            firstReceipt.TrySetResult(text);
        }

        public void CompleteTurn(SessionTurnOutcome? outcome = null) =>
            heldTurn.TrySetResult(outcome ?? new SessionTurnOutcome("finished", SessionReplyStatus.Completed, null, 1));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string tempDirectory;

        private Harness(string tempDirectory, SqliteSessionStore store, AgentHost host, AgentInstance root, FakeEndpoint endpoint, CountingProvider provider)
        {
            this.tempDirectory = tempDirectory;
            Store = store;
            Host = host;
            Root = root;
            Endpoint = endpoint;
            Provider = provider;
        }

        public SqliteSessionStore Store { get; }

        public AgentHost Host { get; }

        public AgentInstance Root { get; }

        public FakeEndpoint Endpoint { get; }

        public CountingProvider Provider { get; }

        public string WorkDirectory => Path.Combine(tempDirectory, WorkDirectoryName);

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "forge-session-messaging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new SqliteSessionStore(directory);
            var rootRow = await store.CreateSessionAsync(Path.Combine(directory, WorkDirectoryName), "Root", CancellationToken.None);
            var endpoint = new FakeEndpoint();
            var provider = new CountingProvider();
            var host = new AgentHost(new AgentHostOptions
            {
                Providers = _ => provider,
                Tools = [],
                Layers = new PromptLayers("sys", null, null),
                Agents = [ModelAgent],
                Store = store,
                SessionMessages = () => endpoint,
            });
            var root = host.CreateRoot(rootRow.Id, rootRow.WorkspacePath);
            return new Harness(directory, store, host, root, endpoint, provider);
        }

        public async Task<SessionInfo> AddUserSessionAsync(string title = "Worker")
        {
            var row = await ((ISessionCatalog)Store).CreateAsync(
                new SessionCreate(WorkDirectory, title)
                {
                    ParentSessionId = Root.SessionId,
                    AgentKind = UserAgent.Name,
                    Task = "task",
                    ChildDriver = "user",
                },
                CancellationToken.None);
            await Host.ResumeUserChildAsync(row.Id, row.WorkspacePath, UserAgent, CancellationToken.None);
            return row;
        }

        public async ValueTask DisposeAsync()
        {
            // End any held background mailbox drain before closing its store. Tests that enqueue a delivery wait for its
            // receipt explicitly; completing here also releases the endpoint if an assertion failed first.
            Endpoint.CompleteTurn();
            await Store.DisposeAsync();
            SqliteConnectionCleanup();
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may retain a pooled native handle until process shutdown; the test's data lives only in this temp tree.
            }
        }

        private static void SqliteConnectionCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class NamedTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "test tool", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("unused"));
    }

    private static ToolContext RootContext(Harness harness) => new(harness.Root.SessionId, harness.Root.WorkingDirectory);

    private static ToolContext ChildContext(Harness harness, SessionInfo child) => new(child.Id, child.WorkspacePath, 1);

    private static Task<ToolResult> SendAsync(Harness harness, ToolContext context, string target, string message = "inspect the change") =>
        harness.Host.SessionMessaging.SendAsync(context, target, message, CancellationToken.None);

    [Fact]
    public void Tool_contract_keeps_the_name_and_strict_target_message_schema()
    {
        Assert.Equal("message_session", SessionReplies.ToolName);
        using var schema = JsonDocument.Parse(SessionReplies.InputSchemaJson);
        var root = schema.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.Equal(new[] { "target", "message" }, root.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal("string", root.GetProperty("properties").GetProperty("target").GetProperty("type").GetString());
        Assert.Equal("string", root.GetProperty("properties").GetProperty("message").GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Missing_grant_is_not_delivered_grant_allows_delivery_and_revoke_blocks_the_same_pair()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();

        var denied = await SendAsync(harness, RootContext(harness), "Worker");
        Assert.True(denied.IsError);
        Assert.Contains("No authorization", denied.Text);
        Assert.Empty(harness.Endpoint.Requests);

        Assert.Equal(harness.Root.SessionId, harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title));
        var sent = await SendAsync(harness, RootContext(harness), "Worker");
        Assert.False(sent.IsError);
        Assert.Equal("已投递 #1（未完成，你现在没有结果）", sent.Text);
        var request = await harness.Endpoint.FirstRequest;
        Assert.Equal(child.Id, request.SessionId);
        Assert.Equal("inspect the change", request.Message);

        harness.Endpoint.CompleteTurn();
        Assert.StartsWith("[会话回复 #1 · 来自 Worker · 状态:完成", await harness.Endpoint.FirstReceipt);

        Assert.True(harness.Host.SessionMessaging.Authorizations.Revoke(harness.Root.SessionId, child.Id));
        var revoked = await SendAsync(harness, RootContext(harness), "Worker");
        Assert.True(revoked.IsError);
        Assert.Contains("No authorization", revoked.Text);
        Assert.Single(harness.Endpoint.Requests);
    }

    [Fact]
    public async Task RevokeTarget_removes_that_target_pair_and_revokeRoot_removes_every_pair_in_its_tree()
    {
        await using var harness = await Harness.CreateAsync();
        var first = await harness.AddUserSessionAsync("First");
        var second = await harness.AddUserSessionAsync("Second");

        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, first.Id, first.Title);
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, second.Id, second.Title);
        harness.Host.SessionMessaging.Grant(first.Id, harness.Root.SessionId, "Root");

        Assert.Equal(1, harness.Host.SessionMessaging.Authorizations.RevokeTarget(first.Id));
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, first.Id));
        Assert.True(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, second.Id));
        Assert.True(harness.Host.SessionMessaging.Authorizations.IsGranted(first.Id, harness.Root.SessionId));

        var targetRevoked = await SendAsync(harness, RootContext(harness), "First");
        Assert.True(targetRevoked.IsError);
        Assert.Contains("No authorization", targetRevoked.Text);

        var delivered = await SendAsync(harness, RootContext(harness), "Second");
        Assert.Equal("已投递 #1（未完成，你现在没有结果）", delivered.Text);
        await harness.Endpoint.FirstRequest;
        harness.Endpoint.CompleteTurn();
        await harness.Endpoint.FirstReceipt;

        Assert.Equal(2, harness.Host.SessionMessaging.RevokeRoot(harness.Root.SessionId));
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, second.Id));
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(first.Id, harness.Root.SessionId));
        var rootRevoked = await SendAsync(harness, RootContext(harness), "Second");
        Assert.True(rootRevoked.IsError);
        Assert.Contains("No authorization", rootRevoked.Text);
        Assert.Single(harness.Endpoint.Requests);
    }

    [Fact]
    public async Task Target_is_resolved_by_unique_user_session_name_and_rejects_ambiguous_missing_and_model_children()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.AddUserSessionAsync("Duplicate");
        await harness.AddUserSessionAsync("Duplicate");
        var modelResult = await harness.Host.SpawnChildAsync(
            harness.Root,
            ModelAgent.Name,
            "model task",
            new EventSink(),
            CancellationToken.None);
        Assert.False(modelResult.IsError);
        var modelAgentId = Assert.Single(harness.Host.SubAgents.Instances, instance => instance.Definition.Name == ModelAgent.Name).Id;
        var modelSession = Assert.IsType<SessionInfo>(await harness.Store.GetSessionAsync(modelResult.ChildSessionId, CancellationToken.None));
        Assert.Null(modelSession.ChildDriver);

        var ambiguous = await SendAsync(harness, RootContext(harness), "Duplicate");
        Assert.True(ambiguous.IsError);
        Assert.Contains("ambiguous", ambiguous.Text);
        var missing = await SendAsync(harness, RootContext(harness), "No Such Session");
        Assert.True(missing.IsError);
        Assert.Contains("There is no agent session named", missing.Text);

        // This child came through the model-spawn path and is present in the stored tree, but is not a user-driven target.
        // Its stored display title and its actual model-visible Agent id must both fail target resolution.
        foreach (var modelIdentifier in new[] { modelSession.Title, modelAgentId })
        {
            var modelChild = await SendAsync(harness, RootContext(harness), modelIdentifier);
            Assert.True(modelChild.IsError);
            Assert.Contains("There is no agent session named", modelChild.Text);
            Assert.DoesNotContain("No authorization", modelChild.Text);
        }

        Assert.Empty(harness.Endpoint.Requests);
    }

    [Fact]
    public async Task A_non_active_target_is_refused_and_its_pair_is_revoked()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        Assert.True(await ((ISessionCatalog)harness.Store).SetStatusAsync(child.Id, SessionStatus.Archived, CancellationToken.None));

        var result = await SendAsync(harness, RootContext(harness), "Worker");

        Assert.True(result.IsError);
        Assert.Contains("no longer active", result.Text);
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, child.Id));
        Assert.Empty(harness.Endpoint.Requests);
    }

    [Fact]
    public async Task Delivery_returns_while_the_target_turn_never_completes_and_marks_busy_root_delivery_as_queued()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(child.Id, harness.Root.SessionId, "Root");
        harness.Endpoint.RootBusy = true;

        // heldTurn is intentionally never completed until after SendAsync has returned. WaitAsync is only a deadlock
        // watchdog: it does not coordinate the test or determine which response is expected.
        var send = harness.Host.SessionMessaging.SendAsync(ChildContext(harness, child), "Root", "please review", CancellationToken.None);
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsError);
        Assert.Equal("已排队 #1（未完成，你现在没有结果）", result.Text);
        var request = await harness.Endpoint.FirstRequest;
        Assert.Equal(harness.Root.SessionId, request.SessionId);
        Assert.Equal("please review", request.Message);

        harness.Endpoint.CompleteTurn();
        Assert.StartsWith("[会话回复 #1 · 来自 Root · 状态:完成", await harness.Endpoint.FirstReceipt);
    }

    [Fact]
    public async Task An_idle_initiator_receives_a_persisted_receipt_without_starting_a_model_turn()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        Task? appended = null;
        harness.Endpoint.OnReceipt = text => appended = harness.Host.AppendSessionMessageAsync(harness.Root.SessionId, text);

        var result = await SendAsync(harness, RootContext(harness), "Worker");
        Assert.Equal("已投递 #1（未完成，你现在没有结果）", result.Text);
        Assert.Equal(child.Id, (await harness.Endpoint.FirstRequest).SessionId);

        harness.Endpoint.CompleteTurn(new SessionTurnOutcome("finished report", SessionReplyStatus.Completed, null, 1));
        var receipt = await harness.Endpoint.FirstReceipt;
        Assert.Contains("finished report", receipt);
        Assert.NotNull(appended);
        await appended!;

        Assert.Single(harness.Endpoint.Requests);
        Assert.Equal(child.Id, harness.Endpoint.Requests.Single().SessionId);
        Assert.Equal(0, harness.Provider.Calls);
        var rootHistory = harness.Root.History;
        Assert.Equal(receipt, Assert.IsType<TextBlock>(rootHistory.Single().Content.Single()).Text);

        var stored = new List<StoredMessage>();
        await foreach (var message in harness.Store.ReadAsync(harness.Root.SessionId, 1, null, CancellationToken.None))
        {
            stored.Add(message);
        }

        Assert.Single(stored);
        Assert.Equal(receipt, Assert.IsType<TextBlock>(stored[0].Message.Content.Single()).Text);
    }

    [Fact]
    public void Root_and_user_session_tool_selections_include_message_session_but_model_child_does_not()
    {
        var messageTool = new NamedTool(SessionReplies.ToolName);
        var registry = new ToolRegistry([messageTool]);
        var subAgentTools = new[] { "spawn_agent", "subagent_stop" };

        var root = registry.Select(null, canSpawn: true, subAgentTools, isRoot: true, canMessage: true);
        var userSession = registry.Select([], canSpawn: true, subAgentTools, isRoot: false, canMessage: true);
        var modelChild = registry.Select([SessionReplies.ToolName], canSpawn: true, subAgentTools, isRoot: false, canMessage: false);

        Assert.Contains(root.Definitions, tool => tool.Name == SessionReplies.ToolName);
        Assert.Contains(userSession.Definitions, tool => tool.Name == SessionReplies.ToolName);
        Assert.DoesNotContain(modelChild.Definitions, tool => tool.Name == SessionReplies.ToolName);
    }

    [Fact]
    public void Receipt_has_the_fixed_three_lines_and_failure_or_stop_carries_a_flattened_reason()
    {
        var complete = SessionReplies.Build("7", "Worker", SessionReplyStatus.Completed, null, "inspect", 2, 5, 3, "done");
        Assert.Equal(
            "[会话回复 #7 · 来自 Worker · 状态:完成 · 任务:inspect]\n改动: 2 个文件 (+5/−3)\n报告: done",
            complete);
        Assert.Equal(3, complete.Split('\n').Length);

        var failed = SessionReplies.Build("8", "Worker", SessionReplyStatus.Failed, "disk\nwrite failed", "inspect", 0, 0, 0, "partial");
        Assert.Equal("[会话回复 #8 · 来自 Worker · 状态:失败(disk write failed) · 任务:inspect]", failed.Split('\n')[0]);
        Assert.Equal(3, failed.Split('\n').Length);

        var stopped = SessionReplies.Build("9", "Worker", SessionReplyStatus.Stopped, "user stopped", "inspect", 0, 0, 0, "partial");
        Assert.Equal("[会话回复 #9 · 来自 Worker · 状态:已停止(user stopped) · 任务:inspect]", stopped.Split('\n')[0]);
        Assert.Equal(3, stopped.Split('\n').Length);
    }

    [Fact]
    public void Truncated_receipts_require_an_artifact_id_for_both_character_and_line_caps()
    {
        var tooManyCharacters = new string('x', SessionReplies.ReportCharLimit + 1);
        var tooManyLines = string.Join('\n', Enumerable.Repeat("line", SessionReplies.ReportLineLimit + 1));

        Assert.Throws<ArgumentException>(() => SessionReplies.Build("1", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, tooManyCharacters));
        Assert.Throws<ArgumentException>(() => SessionReplies.Build("2", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, tooManyLines));

        var charReceipt = SessionReplies.Build("1", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, tooManyCharacters, "artifact-char");
        Assert.Contains("\n完整报告: artifact-char", charReceipt);
        Assert.Equal(SessionReplies.ReportCharLimit, charReceipt.Split("\n报告: ", StringSplitOptions.None)[1].Split('\n')[0].Length);

        var lineReceipt = SessionReplies.Build("2", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, tooManyLines, "artifact-lines");
        Assert.Contains("\n完整报告: artifact-lines", lineReceipt);
        Assert.Equal(SessionReplies.ReportLineLimit, lineReceipt.Split("\n报告: ", StringSplitOptions.None)[1].Split('\n').Count(line => line != "完整报告: artifact-lines"));
    }

    [Fact]
    public void Blank_artifact_ids_are_omitted_or_rejected_and_newline_ids_are_flattened()
    {
        var blank = SessionReplies.Build("1", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, "short report", " \t\n ");
        Assert.Equal("[会话回复 #1 · 来自 Worker · 状态:完成 · 任务:task]\n改动: 0 个文件 (+0/−0)\n报告: short report", blank);
        Assert.Throws<ArgumentException>(() => SessionReplies.Build(
            "2", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0,
            new string('x', SessionReplies.ReportCharLimit + 1), "  \n\t"));

        var newlineId = SessionReplies.Build("3", "Worker", SessionReplyStatus.Completed, null, "task", 0, 0, 0, "short report", "\nartifact\nid\n");
        Assert.EndsWith("\n完整报告: artifact id", newlineId);
        Assert.Equal(4, newlineId.Split('\n').Length);
    }
}
