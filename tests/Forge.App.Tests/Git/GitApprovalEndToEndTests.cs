using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Tests.Approval;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The approval path of the git tools through the real wiring: a <see cref="SessionRuntime"/> with the app's session
/// policy and a real <see cref="ApprovalServices"/> (queue, rules, <see cref="SessionApprovalHandler"/>) drives scripted
/// model turns that call the git tools. <c>git_branch</c> (a write) produces an approval card carrying its tool name,
/// working directory and arguments, runs after Allow and never runs after Deny; the read-only <c>git_status</c> runs
/// without a card; without approval wiring a write is denied by the loop's own gate. The branch itself is checked on disk,
/// so a tool that "looks" approved but did not run (or ran although denied) is caught.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitApprovalEndToEndTests
{
    private const string CreateBranchArguments = """{"operation":"create","name":"agent/feature"}""";

    [Fact]
    public async Task Approving_the_git_branch_card_runs_the_tool()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        await RunScriptedTurnAsync(
            session,
            work,
            ScriptedGitProvider.Call("call-1", GitTools.BranchName, CreateBranchArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(GitTools.BranchName, card.ToolName);
                Assert.Equal(Path.GetFullPath(work), Path.GetFullPath(card.WorkingDirectory), StringComparer.OrdinalIgnoreCase);

                // The arguments are formatted on a pool thread and published back to the UI thread.
                await GitPanelTesting.WaitUntilAsync(() => card.HasArguments, "the card's formatted arguments");
                Assert.Contains("agent/feature", card.ArgumentsText, StringComparison.Ordinal);
                Assert.Contains("create", card.ArgumentsText, StringComparison.Ordinal);
                Assert.Equal(1, await queue.CountAsync());

                // Nothing has run while the card is open.
                Assert.DoesNotContain("agent/feature", GitTestShell.BranchList(work));
                Assert.DoesNotContain(events, static e => e is AgentToolFinished);

                card.AllowCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Contains("agent/feature", GitTestShell.BranchList(work));
                var finished = Assert.Single(events.OfType<AgentToolFinished>());
                Assert.Equal(GitTools.BranchName, finished.Call.Name);
                Assert.False(finished.Result.IsError, finished.Result.Content);
                Assert.DoesNotContain(events, static e => e is AgentApprovalDenied);
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task Denying_the_git_branch_card_never_runs_the_tool()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        await RunScriptedTurnAsync(
            session,
            work,
            ScriptedGitProvider.Call("call-1", GitTools.BranchName, CreateBranchArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(GitTools.BranchName, card.ToolName);

                card.DenyCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.DoesNotContain("agent/feature", GitTestShell.BranchList(work));
                var denied = Assert.Single(events.OfType<AgentApprovalDenied>());
                Assert.Equal(GitTools.BranchName, denied.Call.Name);
                Assert.DoesNotContain(events, static e => e is AgentToolFinished { Result.IsError: false });
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task A_read_only_git_status_call_needs_no_card_and_runs()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        await RunScriptedTurnAsync(
            session,
            work,
            ScriptedGitProvider.Call("call-1", GitTools.StatusName, "{}"),
            async (turn, queue, events) =>
            {
                // No card is ever added: the turn runs to its end on its own.
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.False(turn.CardSeen, "a read-only tool must not raise an approval card");
                Assert.Equal(0, await queue.CountAsync());
                var finished = Assert.Single(events.OfType<AgentToolFinished>());
                Assert.Equal(GitTools.StatusName, finished.Call.Name);
                Assert.False(finished.Result.IsError, finished.Result.Content);
                Assert.Contains("main", finished.Result.Content, StringComparison.Ordinal);
                Assert.DoesNotContain(events, static e => e is AgentApprovalDenied);
            });
    }

    [Fact]
    public async Task Without_approval_wiring_a_git_write_is_denied_by_the_loop_gate()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var session = SharedUi.Session;
        await RunScriptedTurnAsync(
            session,
            work,
            ScriptedGitProvider.Call("call-1", GitTools.BranchName, CreateBranchArguments),
            async (turn, queue, events) =>
            {
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.DoesNotContain("agent/feature", GitTestShell.BranchList(work));
                Assert.Single(events.OfType<AgentApprovalDenied>());
                Assert.False(turn.CardSeen);
                Assert.Equal(0, await queue.CountAsync());
            },
            withApprovals: false);
    }

    // ------------------------------------------------------------ rig

    private sealed class TurnHandle
    {
        private readonly Task<ToolApprovalCardViewModel> card;

        public TurnHandle(Task completion, Task<ToolApprovalCardViewModel> card)
        {
            Completion = completion;
            this.card = card;
        }

        public Task Completion { get; }

        public bool CardSeen => card.IsCompleted;

        public Task<ToolApprovalCardViewModel> NextCardAsync() => card.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task RunScriptedTurnAsync(
        HeadlessUnitTestSession session,
        string work,
        IReadOnlyList<ChatStreamEvent> firstResponse,
        Func<TurnHandle, ApprovalQueue, ConcurrentQueue<AgentEvent>, Task> body,
        bool withApprovals = true)
    {
        await using var root = ApprovalHeadlessRoot.Create();
        var dataDirectory = Directory.CreateDirectory(Path.Combine(root.Root, "sessions")).FullName;
        var store = new SqliteSessionStore(dataDirectory);
        var todos = new SqliteTodoStore(dataDirectory);
        try
        {
            await session.Dispatch(
                async () =>
                {
                    var services = ApprovalServices.Create(AppPaths.Create(), host: null, hostProblem: default);
                    SessionRuntime? runtime = null;
                    try
                    {
                        var info = await store.CreateSessionAsync(work, "approval e2e", CancellationToken.None);
                        var provider = new ScriptedGitProvider(firstResponse);
                        var runtimeServices = new AgentRuntimeServices(
                            _ => provider,
                            null,
                            store,
                            store,
                            todos,
                            new ModelSettings("test"),
                            Git: new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()))
                        {
                            Approvals = withApprovals ? services : null,
                        };
                        runtime = new SessionRuntime(runtimeServices, info);

                        // Subscribe before the turn starts so no card is missed.
                        var cardTask = ApprovalFixtures.NextToolCardAsync(services.Queue);
                        var events = new ConcurrentQueue<AgentEvent>();
                        var completion = runtime.RunTurnAsync("go", events.Enqueue, CancellationToken.None);
                        try
                        {
                            await body(new TurnHandle(completion, cardTask), services.Queue, events);
                        }
                        finally
                        {
                            // A failed assertion must not leave the turn waiting on a card for ever.
                            await services.Queue.WithdrawAllAsync();
                            await completion.WaitAsync(TimeSpan.FromSeconds(30));
                        }
                    }
                    finally
                    {
                        runtime?.Dispose();
                        await services.DisposeAsync();
                    }

                    return 0;
                },
                CancellationToken.None);
        }
        finally
        {
            store.Dispose();
            todos.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>Turn 0 answers with the scripted tool call(s); every later turn closes the conversation.</summary>
    private sealed class ScriptedGitProvider(IReadOnlyList<ChatStreamEvent> first) : IChatProvider
    {
        private int calls;

        public string Id => "scripted-git";

        public static IReadOnlyList<ChatStreamEvent> Call(string id, string name, string arguments) =>
        [
            new MessageCompleted(
                new ChatMessage(MessageRole.Assistant, [new TextBlock("calling"), new ToolCallBlock(id, name, arguments)]),
                new TokenUsage(10, 5),
                StopReason.ToolUse),
        ];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            var index = Interlocked.Increment(ref calls) - 1;
            IReadOnlyList<ChatStreamEvent> response = index == 0
                ?
                first
                :
                [
                    new TextDelta("done"),
                    new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(10, 5), StopReason.EndTurn),
                ];
            foreach (var value in response)
            {
                await Task.Yield();
                yield return value;
            }
        }
    }
}
