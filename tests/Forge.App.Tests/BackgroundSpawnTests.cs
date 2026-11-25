using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// <c>spawn_agent</c> never waits for its child: the parent's turn goes on (and ends) while the child works - the whole point being
/// that the user can hand the parent a new task meanwhile - and the child's report comes back as a reply message when it ends.
/// <para>Driven through the real runtime, loop and scheduler with a scripted provider: what is asserted is the parent's turn
/// finishing while the child is still gated, and the receipt text that reaches the app's delivery seam.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class BackgroundSpawnTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("forge-background-spawn-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public Task A_spawn_leaves_the_parent_free_and_reports_back_as_a_message() =>
        SharedUi.RunAsync(async () =>
        {
            var store = new SqliteSessionStore(directory);
            var root = await ((ISessionCatalog)store).CreateAsync(
                new SessionCreate(directory, "background spawn"),
                CancellationToken.None);
            var provider = new ScriptedProvider();
            var receipts = new Receipts();
            var runtime = new SessionRuntime(
                new AgentRuntimeServices(
                    ProviderFactory: _ => provider,
                    HostClient: null,
                    Store: store,
                    Catalog: store,
                    Todos: new SqliteTodoStore(directory),
                    Model: new ModelSettings("test-model"))
                {
                    SessionMessages = () => receipts,
                },
                root);
            using var http = new HttpClient();
            var tab = new SessionWorkspaceViewModel(
                root,
                runtime,
                checkpoints: null,
                store,
                new ProviderServices(Path.Combine(directory, "settings.json"), directory, http))
            {
                AutoResumeDelay = TimeSpan.FromMinutes(5),
            };

            var turns = 0;
            var parentDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.TurnCompleted += _ =>
            {
                if (Interlocked.Increment(ref turns) == 1)
                {
                    parentDone.TrySetResult();
                }
            };

            try
            {
                // The roster arrives through the frame coalescer, so the case subscribes before anything runs and waits for the
                // roster's own event (or the state it already reached) rather than for the turn end.
                var rosterChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.SubAgentsChanged += () =>
                {
                    if (tab.SubAgents.Count > 0)
                    {
                        rosterChanged.TrySetResult();
                    }
                };

                tab.Draft = "delegate the analysis and keep going";
                tab.SendCommand.Execute(null);

                // The child's own request is gated: the parent's turn ends anyway, with the child still running.
                var parentCard = await WaitForTurnEndAsync(tab, parentDone);
                Assert.Equal(TurnEndStatus.Completed, parentCard.EndStatus);
                Assert.False(provider.ChildReleased);

                // The parent's tool result already said there is no report yet, and the child is a live sub-agent of this tab.
                if (tab.SubAgents.Count > 0)
                {
                    rosterChanged.TrySetResult();
                }

                await rosterChanged.Task.WaitAsync(TimeSpan.FromMinutes(1));
                var spawned = Assert.Single(tab.SubAgents);
                Assert.Equal(SubAgentStatus.Running, spawned.Status);
                Assert.Equal("Worker", spawned.Kind);

                // Let the child finish: its report reaches the app's delivery seam as one reply message, not as a turn, and the tab
                // learns that it finished even though the turn that spawned it is long over (its events travel on the session's own
                // sink, not on that turn's channel, which was completed when the turn ended).
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                tab.SubAgentsChanged += () =>
                {
                    if (spawned.Status == SubAgentStatus.Completed)
                    {
                        finished.TrySetResult();
                    }
                };
                provider.ReleaseChild();
                var receipt = await Receipts.WaitAsync(receipts, TimeSpan.FromMinutes(1));
                Assert.Equal(root.Id, receipt.SessionId);
                Assert.StartsWith(SessionReplies.Marker, receipt.Text, StringComparison.Ordinal);
                Assert.Contains("child report: the analysis is done", receipt.Text, StringComparison.Ordinal);
                Assert.Contains("状态:完成", receipt.Text, StringComparison.Ordinal);
                Assert.Equal(1, Volatile.Read(ref turns));

                if (spawned.Status != SubAgentStatus.Completed)
                {
                    await finished.Task.WaitAsync(TimeSpan.FromMinutes(1));
                }

                Assert.Equal(SubAgentStatus.Completed, spawned.Status);

                // What the model was told when the spawn returned: no report yet, and the way to steer or stop the child.
                lock (provider.ParentRequests)
                {
                    Assert.Equal(2, provider.ParentRequests.Count);
                    var toolMessage = Assert.Single(provider.ParentRequests[1], static message => message.Role == MessageRole.Tool);
                    var toolResult = Assert.IsType<ToolResultBlock>(Assert.Single(toolMessage.Content));
                    Assert.Contains("still running, so there is no report yet", toolResult.Text, StringComparison.Ordinal);
                    Assert.Contains("subagent_steer", toolResult.Text, StringComparison.Ordinal);
                    Assert.False(toolResult.IsError);
                }
            }
            finally
            {
                provider.ReleaseChild();
                tab.Dispose();
                store.Dispose();
            }
        });

    /// <summary>
    /// The receipt built when the report is past the caps and no artifact store can hold it: <see cref="SessionReplies.Cut"/> has to
    /// leave room for its own note, or the strict builder throws and the parent never hears from the child at all.
    /// </summary>
    [Fact]
    public void A_capped_report_without_an_artifact_still_builds_a_receipt()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 400).Select(static line => new string('x', 40) + line));
        var cut = SessionReplies.Cut(lines);

        // Both caps and the note that says it was cut: the builder below enforces them and throws when they are exceeded.
        Assert.True(cut.Length <= SessionReplies.ReportCharLimit);
        Assert.True(cut.Split('\n').Length <= SessionReplies.ReportLineLimit);
        Assert.EndsWith(SessionReplies.CutNote, cut, StringComparison.Ordinal);

        var receipt = SessionReplies.BuildChildReport(
            "Worker",
            "Worker-1",
            SessionReplyStatus.Completed,
            reason: null,
            "analysed the repository",
            changedFiles: 0,
            addedLines: 0,
            removedLines: 0,
            report: cut);
        Assert.StartsWith(SessionReplies.Marker, receipt, StringComparison.Ordinal);
        Assert.EndsWith(SessionReplies.CutNote, receipt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stopping the session ends its background children through the host's own registry - not through the tab's sub-agent rows,
    /// which the frame coalescer may not have filled yet - and the report that comes back names the stop instead of claiming the
    /// reason is unknown.
    /// </summary>
    [Fact]
    public Task Stopping_the_session_ends_the_background_child_and_says_why() =>
        SharedUi.RunAsync(async () =>
        {
            var store = new SqliteSessionStore(directory);
            var root = await ((ISessionCatalog)store).CreateAsync(
                new SessionCreate(directory, "background stop"),
                CancellationToken.None);
            var provider = new ScriptedProvider();
            var receipts = new Receipts();
            var runtime = new SessionRuntime(
                new AgentRuntimeServices(
                    ProviderFactory: _ => provider,
                    HostClient: null,
                    Store: store,
                    Catalog: store,
                    Todos: new SqliteTodoStore(directory),
                    Model: new ModelSettings("test-model"))
                {
                    SessionMessages = () => receipts,
                },
                root);
            using var http = new HttpClient();
            var tab = new SessionWorkspaceViewModel(
                root,
                runtime,
                checkpoints: null,
                store,
                new ProviderServices(Path.Combine(directory, "settings.json"), directory, http))
            {
                AutoResumeDelay = TimeSpan.FromMinutes(5),
            };

            var parentDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.TurnCompleted += _ => parentDone.TrySetResult();

            try
            {
                tab.Draft = "delegate the analysis and keep going";
                tab.SendCommand.Execute(null);
                await WaitForTurnEndAsync(tab, parentDone);

                // The child is gated and running; the user stops the session while it is.
                tab.Cancel();

                var receipt = await Receipts.WaitAsync(receipts, TimeSpan.FromMinutes(1));
                Assert.StartsWith(SessionReplies.Marker, receipt.Text, StringComparison.Ordinal);
                Assert.Contains("状态:已停止", receipt.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("原因未知", receipt.Text, StringComparison.Ordinal);
                Assert.Contains(SessionStrings.SubAgentStoppedByUser, receipt.Text, StringComparison.Ordinal);
            }
            finally
            {
                provider.ReleaseChild();
                tab.Dispose();
                store.Dispose();
            }
        });

    /// <summary>
    /// The report's delivery with no app seam at all (a script, a test, a window that failed to install one): the parent's own
    /// history is written, so the next turn - and a reopened session - reads what the child said. Also the case where the parent
    /// instance is not held any more (its tab was closed): the store is the one place left, and the message still lands there.
    /// </summary>
    [Fact]
    public Task A_report_without_an_app_seam_lands_in_the_parent_history() =>
        SharedUi.RunAsync(async () =>
        {
            var store = new SqliteSessionStore(directory);
            var root = await ((ISessionCatalog)store).CreateAsync(
                new SessionCreate(directory, "background without a seam"),
                CancellationToken.None);
            var host = new AgentHost(new AgentHostOptions
            {
                Providers = static _ => throw new InvalidOperationException("no model is asked here"),
                Tools = [],
                Layers = new PromptLayers("sys", null, null),
                Store = store,
            });
            var instance = host.CreateRoot(root.Id, directory);

            await host.DeliverBackgroundReportAsync(new BackgroundChildReport(
                root.Id,
                instance.Id,
                "Worker-1",
                "child-session",
                "Worker",
                "analysed the repository",
                SessionReplyStatus.Completed,
                Reason: null,
                Report: "the analysis is done"));

            var texts = new List<string>();
            await foreach (var stored in store.ReadAsync(root.Id, fromSeq: 0, toSeq: null, CancellationToken.None))
            {
                if (stored.Message.Role == MessageRole.User)
                {
                    texts.AddRange(stored.Message.Content.OfType<TextBlock>().Select(static block => block.Text));
                }
            }

            var receipt = Assert.Single(texts, static text => text.StartsWith(SessionReplies.Marker, StringComparison.Ordinal));
            Assert.Contains("子 Agent Worker (Worker-1)", receipt, StringComparison.Ordinal);
            Assert.Contains("状态:完成", receipt, StringComparison.Ordinal);
            Assert.Contains("the analysis is done", receipt, StringComparison.Ordinal);

            // The live instance's own history carries it too: that is what this host's next turn reads.
            var inMemory = Assert.Single(
                instance.History,
                static message => message.Role == MessageRole.User
                    && message.Content.OfType<TextBlock>().Any(static block => block.Text.StartsWith(SessionReplies.Marker, StringComparison.Ordinal)));
            Assert.Equal(receipt, inMemory.Content.OfType<TextBlock>().Last().Text);
            store.Dispose();
        });

    /// <summary>Waits for the tab's first turn to close, pumping the dispatcher meanwhile.</summary>
    /// <summary>The last user message a request carries: what tells the parent's requests from the child's in these scripts.</summary>
    private static string LastUserText(ChatRequest request)
    {
        for (var index = request.Messages.Count - 1; index >= 0; index--)
        {
            if (request.Messages[index].Role != MessageRole.User)
            {
                continue;
            }

            foreach (var block in request.Messages[index].Content)
            {
                if (block is TextBlock text)
                {
                    return text.Text;
                }
            }
        }

        return string.Empty;
    }

    private static async Task<TurnCardViewModel> WaitForTurnEndAsync(SessionWorkspaceViewModel tab, TaskCompletionSource done)
    {
        await done.Task.WaitAsync(TimeSpan.FromMinutes(1));
        return Assert.IsType<TurnCardViewModel>(tab.Transcript.Current);
    }

    /// <summary>The app's delivery seam: records what Core hands it instead of drawing anything.</summary>
    private sealed class Receipts : ISessionMessageEndpoint
    {
        private readonly TaskCompletionSource<(string SessionId, string Text)> first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string SessionId, string Text)> Delivered { get; } = [];

        public static async Task<(string SessionId, string Text)> WaitAsync(Receipts receipts, TimeSpan timeout) =>
            await receipts.first.Task.WaitAsync(timeout);

        public bool IsBusy(string sessionId) => false;

        public Task<SessionTurnOutcome?> RunSessionTurnAsync(SessionTurnRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("a background child's report never runs a turn");

        public Task<int> BeginSessionTurnAsync(string sessionId, string workingDirectory, CancellationToken ct) => Task.FromResult(1);

        public Task EndSessionTurnAsync(string sessionId, int turn, SessionReplyStatus status, string? reason, string workingDirectory, long lastSeq, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<SessionTurnChanges?> ReadChangesAsync(string sessionId, int turn, CancellationToken ct) =>
            Task.FromResult<SessionTurnChanges?>(null);

        public bool AcceptsReceipts => true;

        public void DeliverReceipt(string initiatorSessionId, string text)
        {
            lock (Delivered)
            {
                Delivered.Add((initiatorSessionId, text));
            }

            first.TrySetResult((initiatorSessionId, text));
        }
    }

    /// <summary>
    /// Answers three kinds of request: the parent's first one with the spawn, the parent's second one with its own
    /// answer, and the child's with a report the test releases when it wants the child to end.
    /// </summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int parentCalls;

        /// <summary>Every request the parent made, in order: the second one carries the tool result the spawn answered with.</summary>
        public List<IReadOnlyList<ChatMessage>> ParentRequests { get; } = [];

        public string Id => "scripted";

        public bool ChildReleased => release.Task.IsCompleted;

        public void ReleaseChild() => release.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (LastUserText(request).Contains("CHILD TASK", StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(ct);
                yield return new TextDelta("child report: the analysis is done");
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("child report: the analysis is done")]),
                    TokenUsage.Empty,
                    StopReason.EndTurn);
                yield break;
            }

            lock (ParentRequests)
            {
                ParentRequests.Add(request.Messages);
            }

            if (Interlocked.Increment(ref parentCalls) == 1)
            {
                var call = new ToolCallBlock(
                    "spawn-1",
                    "spawn_agent",
                    """{"kind":"Worker","task":"CHILD TASK: analyse the repository"}""");
                yield return new ToolCallStarted(call.Id, call.Name);
                yield return new ToolCallArgumentsDelta(call.Id, call.ArgumentsJson);
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [call]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
                yield break;
            }

            yield return new TextDelta("parent kept working");
            yield return new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("parent kept working")]),
                TokenUsage.Empty,
                StopReason.EndTurn);
        }

    }
}
