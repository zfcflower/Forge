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
    private const string RootStopReason = "用户停止了根会话";

    /// <summary>Only keeps a broken wait from hanging the run; nothing in this file coordinates through it.</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    private static readonly AgentDefinition UserAgent = new("Worker", "worker", "worker prompt", new ModelSettings("model"), []);
    private static readonly AgentDefinition ModelAgent = new("ModelWorker", "model worker", "model prompt", new ModelSettings("model"), ["read"]);

    private sealed class CountingProvider : IChatProvider
    {
        private int calls;
        private int blockOnce;
        private TaskCompletionSource? blockEntered;
        private TaskCompletionSource? blockGate;

        public string Id => "unused";

        public int Calls => Volatile.Read(ref calls);

        /// <summary>
        /// Holds the next turn inside the provider until <see cref="ReleaseBlockedTurn"/>, and answers a task that completes
        /// once that turn is there - which is what makes "a delivery runs right now" an exact state instead of a guess. The
        /// wait ignores its token on purpose: the test, not a stop, decides when the held turn goes on, so the state a stop
        /// sees is the one the test built.
        /// </summary>
        public Task BlockNextTurn()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            blockEntered = entered;
            blockGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref blockOnce, 1);
            return entered.Task;
        }

        public void ReleaseBlockedTurn() => blockGate?.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (Interlocked.Exchange(ref blockOnce, 0) == 1)
            {
                blockEntered!.TrySetResult();
                await blockGate!.Task.ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
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
        private readonly TaskCompletionSource turnEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<SessionTurnRequest> requests = new();
        private readonly ConcurrentQueue<string> receipts = new();
        private readonly ConcurrentQueue<string> begins = new();
        private int blockChangesOnce;
        private TaskCompletionSource? changesEntered;
        private TaskCompletionSource? changesGate;

        public Func<string, Task>? OnReceipt { get; set; }

        public bool RootBusy { get; set; }

        /// <summary>
        /// The one session this fake has a live view of: <see cref="RunSessionTurnAsync"/> answers null for every other
        /// session, which is how a user session's delivery becomes Core's own turn. Null keeps the app's view for all.
        /// </summary>
        public string? ViewSessionId { get; set; }

        public IReadOnlyList<SessionTurnRequest> Requests => requests.ToArray();

        public IReadOnlyList<string> Receipts => receipts.ToArray();

        /// <summary>The sessions whose turn checkpoint was opened, in call order.</summary>
        public IReadOnlyList<string> Begins => begins.ToArray();

        /// <summary>Completes when the first checkpoint is closed: by then that turn's run has released its instance.</summary>
        public Task TurnEnded => turnEnded.Task;

        public Task<SessionTurnRequest> FirstRequest => firstRequest.Task;

        public Task<string> FirstReceipt => firstReceipt.Task;

        public bool IsBusy(string sessionId) => RootBusy;

        public Task<SessionTurnOutcome?> RunSessionTurnAsync(SessionTurnRequest request, CancellationToken ct)
        {
            requests.Enqueue(request);
            firstRequest.TrySetResult(request);
            if (ViewSessionId is { } view && !string.Equals(request.SessionId, view, StringComparison.Ordinal))
            {
                return Task.FromResult<SessionTurnOutcome?>(null);
            }

            return heldTurn.Task;
        }

        public Task<int> BeginSessionTurnAsync(string sessionId, string workingDirectory, CancellationToken ct)
        {
            begins.Enqueue(sessionId);
            return Task.FromResult(1);
        }

        public Task EndSessionTurnAsync(string sessionId, int turn, SessionReplyStatus status, string? reason, string workingDirectory, long lastSeq, CancellationToken ct)
        {
            turnEnded.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Holds the next change-line read until <see cref="ReleaseChanges"/>, and answers a task that completes once that
        /// read is there. Entering that read means the turn already returned and its outcome is recorded, so this is the one
        /// window where a delivery is past its turn and still assembling its receipt. The wait ignores its token on purpose:
        /// the test, not a stop, decides when the receipt goes on, so the state the stop sees is the one the test built.
        /// </summary>
        public Task BlockNextChanges()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            changesEntered = entered;
            changesGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref blockChangesOnce, 1);
            return entered.Task;
        }

        public void ReleaseChanges() => changesGate?.TrySetResult();

        public async Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref blockChangesOnce, 0) == 1)
            {
                changesEntered!.TrySetResult();
                await changesGate!.Task.ConfigureAwait(false);
            }

            return new SessionTurnChanges(2, 5, 3);
        }

        public bool AcceptsReceipts => true;

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
            Endpoint.ReleaseChanges();
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

    /// <summary>The receipt's header line, which carries the dispatch number, the status and the task label.</summary>
    private static string Header(string receipt) => receipt.Split('\n')[0];

    /// <summary>
    /// Completes when a receipt for <paramref name="dispatchId"/> reaches the initiator. The fake's hook is a signal, not a
    /// wait: it is installed before the step that produces the receipt, so a receipt already there and one still coming are
    /// the same case.
    /// </summary>
    private static Task<string> NextReceiptOf(Harness harness, string dispatchId)
    {
        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Endpoint.OnReceipt = text =>
        {
            if (text.StartsWith($"[会话回复 {dispatchId} ·", StringComparison.Ordinal))
            {
                arrived.TrySetResult(text);
            }

            return Task.CompletedTask;
        };
        return arrived.Task;
    }

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
    public async Task A_root_that_left_active_refuses_delivery_and_drops_every_grant_of_its_tree()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        Assert.True(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, child.Id));
        Assert.True(await ((ISessionCatalog)harness.Store).SetStatusAsync(
            harness.Root.SessionId,
            SessionStatus.Archived,
            CancellationToken.None));

        var refused = await SendAsync(harness, RootContext(harness), "Worker");

        // A root that left Active authorizes nothing: the failure says so, the pair is gone and no turn was started for it.
        Assert.True(refused.IsError);
        Assert.Contains("The root session is no longer active", refused.Text);
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, child.Id));
        Assert.Empty(harness.Endpoint.Requests);
        Assert.Equal(0, harness.Provider.Calls);

        // The pair itself is gone: bringing the root back to Active does not revive it, so the next attempt is refused for
        // want of authorization instead of running a turn.
        Assert.True(await ((ISessionCatalog)harness.Store).SetStatusAsync(
            harness.Root.SessionId,
            SessionStatus.Active,
            CancellationToken.None));
        var again = await SendAsync(harness, RootContext(harness), "Worker");
        Assert.True(again.IsError);
        Assert.Contains("No authorization", again.Text);
        Assert.Empty(harness.Endpoint.Requests);
        Assert.Equal(0, harness.Provider.Calls);
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
        var result = await send.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("已排队 #1（未完成，你现在没有结果）", result.Text);
        var request = await harness.Endpoint.FirstRequest;
        Assert.Equal(harness.Root.SessionId, request.SessionId);
        Assert.Equal("please review", request.Message);

        harness.Endpoint.CompleteTurn();
        Assert.StartsWith("[会话回复 #1 · 来自 Root · 状态:完成", await harness.Endpoint.FirstReceipt);
    }

    [Fact]
    public async Task A_root_stop_answers_the_queued_and_the_in_flight_delivery_once_each_and_the_mailbox_keeps_working()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        // The app has no live view of this session, so its turn is Core's own - the only place a delivery can be held in
        // flight inside a turn that really runs.
        harness.Endpoint.ViewSessionId = harness.Root.SessionId;
        var inFlight = harness.Provider.BlockNextTurn();

        Assert.Equal(
            "已投递 #1（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker", "first message")).Text);
        await inFlight.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.Equal(
            "已排队 #2（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker", "second message")).Text);

        // The user presses stop on the root while #1 is inside its turn and #2 still waits behind it.
        Assert.Equal(1, harness.Host.StopUserChildren(RootStopReason));

        // One answer per dispatch number, each saying why it ended: a dropped queue would leave #2 silent, and a stop that
        // also let the delivery answer itself would leave #1 with two receipts.
        var receipts = harness.Endpoint.Receipts;
        Assert.Equal(2, receipts.Count);
        var stoppedHeader = $"状态:已停止({RootStopReason})";
        var first = Assert.Single(receipts, receipt => receipt.StartsWith("[会话回复 #1 ·", StringComparison.Ordinal));
        var second = Assert.Single(receipts, receipt => receipt.StartsWith("[会话回复 #2 ·", StringComparison.Ordinal));
        Assert.Equal($"[会话回复 #1 · 来自 Worker · {stoppedHeader} · 任务:first message]", Header(first));
        Assert.Equal($"[会话回复 #2 · 来自 Worker · {stoppedHeader} · 任务:second message]", Header(second));
        Assert.Contains("\n改动: 0 个文件 (+0/−0)", first);
        Assert.Contains("\n改动: 0 个文件 (+0/−0)", second);

        // The mailbox is not wedged: the stopped turn unwinds, and a third delivery runs its own turn and reports it.
        var third = NextReceiptOf(harness, "#3");
        harness.Provider.ReleaseBlockedTurn();
        await harness.Endpoint.TurnEnded.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.False((await SendAsync(harness, RootContext(harness), "Worker", "third message")).IsError);
        var thirdReceipt = await third.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.Equal("[会话回复 #3 · 来自 Worker · 状态:完成 · 任务:third message]", Header(thirdReceipt));
        Assert.Contains("\n改动: 2 个文件 (+5/−3)", thirdReceipt);
        Assert.Contains("报告: model turn completed", thirdReceipt);
        Assert.Equal(2, harness.Provider.Calls);
        // ... and the stopped delivery is not answered again once its turn unwinds: three deliveries, three receipts.
        Assert.Equal(3, harness.Endpoint.Receipts.Count);
        Assert.Single(harness.Endpoint.Receipts, receipt => receipt.StartsWith("[会话回复 #1 ·", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_delivery_queued_behind_a_running_turn_is_refused_before_it_starts_when_its_root_or_target_left_active(bool archiveRoot)
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        harness.Endpoint.ViewSessionId = harness.Root.SessionId;
        var inFlight = harness.Provider.BlockNextTurn();
        Assert.Equal(
            "已投递 #1（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker", "first message")).Text);
        await inFlight.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.Equal(
            "已排队 #2（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker", "second message")).Text);

        // The row leaves Active while #2 is still queued, so the checks its send already passed no longer hold by the time
        // the target finally goes idle.
        var queued = NextReceiptOf(harness, "#2");
        Assert.True(await ((ISessionCatalog)harness.Store).SetStatusAsync(
            archiveRoot ? harness.Root.SessionId : child.Id,
            SessionStatus.Archived,
            CancellationToken.None));
        harness.Provider.ReleaseBlockedTurn();

        var refused = await queued.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        var first = Assert.Single(
            harness.Endpoint.Receipts,
            receipt => receipt.StartsWith("[会话回复 #1 ·", StringComparison.Ordinal));
        Assert.Equal("[会话回复 #1 · 来自 Worker · 状态:完成 · 任务:first message]", Header(first));
        Assert.Equal(
            archiveRoot
                ? "[会话回复 #2 · 来自 Worker · 状态:失败(会话已不再活动（根会话已离开 Active）) · 任务:second message]"
                : "[会话回复 #2 · 来自 Worker · 状态:失败(会话已不再活动（目标会话已归档或关闭）) · 任务:second message]",
            Header(refused));

        // Refused by the recheck, not by the send: no checkpoint was opened for #2 and the model was never called for it.
        Assert.Single(harness.Endpoint.Begins);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Contains("\n改动: 0 个文件 (+0/−0)", refused);
        Assert.False(harness.Host.SessionMessaging.Authorizations.IsGranted(harness.Root.SessionId, child.Id));
    }

    [Fact]
    public async Task A_receipt_that_already_went_out_is_not_rewritten_or_repeated_when_the_root_stop_lands_on_it()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        // The stop lands inside the delivery of the receipt itself: the turn is over and its answer is on the wire, while
        // the mailbox still holds that delivery as its current one. The flag keeps the fake from answering its own receipt
        // with another stop, so what this test measures stays the mailbox's decision and not the fake's recursion.
        var stopping = 0;
        harness.Endpoint.OnReceipt = _ =>
        {
            if (Interlocked.Exchange(ref stopping, 1) == 0)
            {
                harness.Host.SessionMessaging.CancelPending(RootStopReason);
            }

            return Task.CompletedTask;
        };

        Assert.Equal(
            "已投递 #1（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker")).Text);
        harness.Endpoint.CompleteTurn();

        var receipt = await harness.Endpoint.FirstReceipt.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.Equal(
            "[会话回复 #1 · 来自 Worker · 状态:完成 · 任务:inspect the change]\n改动: 2 个文件 (+5/−3)\n报告: finished",
            receipt);
        Assert.Single(harness.Endpoint.Receipts);
    }

    [Fact]
    public async Task A_stop_that_lands_while_a_finished_delivery_reads_its_change_line_keeps_the_real_receipt()
    {
        await using var harness = await Harness.CreateAsync();
        var child = await harness.AddUserSessionAsync();
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);
        var receipt = NextReceiptOf(harness, "#1");
        // Blocking the change-line read is what puts #1 in the window this test pins: its turn has returned and its outcome
        // is recorded, while the receipt is still being put together.
        var readingChanges = harness.Endpoint.BlockNextChanges();

        Assert.Equal(
            "已投递 #1（未完成，你现在没有结果）",
            (await SendAsync(harness, RootContext(harness), "Worker")).Text);
        await harness.Endpoint.FirstRequest;
        harness.Endpoint.CompleteTurn(new SessionTurnOutcome("finished report", SessionReplyStatus.Completed, null, 1));
        await readingChanges.WaitAsync(Watchdog, TestContext.Current.CancellationToken);

        // The user presses stop on the root inside that window: #1's turn is over, so the stop owns nothing of it.
        harness.Host.StopUserChildren(RootStopReason);
        Assert.Empty(harness.Endpoint.Receipts);

        // Released, #1 answers with what its turn really produced - its change line and its report - and not with the stopped
        // shape a rewrite would carry ("状态:已停止…" and 0 changed files).
        harness.Endpoint.ReleaseChanges();
        Assert.Equal(
            "[会话回复 #1 · 来自 Worker · 状态:完成 · 任务:inspect the change]\n改动: 2 个文件 (+5/−3)\n报告: finished report",
            await receipt.WaitAsync(Watchdog, TestContext.Current.CancellationToken));

        // One delivery, one receipt: the stop must not leave a second line next to the real one.
        Assert.Single(harness.Endpoint.Receipts);
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
    public async Task A_user_session_that_has_not_run_a_turn_takes_the_delivery_through_its_tab()
    {
        await using var harness = await Harness.CreateAsync();

        // A session of the branch that this process has never run - the state every Agent session is in right after a restart.
        var child = await ((ISessionCatalog)harness.Store).CreateAsync(
            new SessionCreate(harness.WorkDirectory, "say hi")
            {
                ParentSessionId = harness.Root.SessionId,
                AgentKind = UserAgent.Name,
                Task = "task",
                ChildDriver = "user",
            },
            CancellationToken.None);
        harness.Host.SessionMessaging.Grant(harness.Root.SessionId, child.Id, child.Title);

        var result = await SendAsync(harness, RootContext(harness), "say hi", "say hi");

        Assert.False(result.IsError);
        Assert.Equal("已投递 #1（未完成，你现在没有结果）", result.Text);
        // The app's seam runs the target's turn in its own tab; this host holds no instance of that session at all.
        var request = await harness.Endpoint.FirstRequest;
        Assert.Equal(child.Id, request.SessionId);
        Assert.Equal("say hi", request.Message);

        harness.Endpoint.CompleteTurn();
        Assert.StartsWith("[会话回复 #1 · 来自 say hi · 状态:完成", await harness.Endpoint.FirstReceipt);
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
