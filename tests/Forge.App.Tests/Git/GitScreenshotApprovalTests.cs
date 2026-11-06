using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Tests.Approval;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The approval path of the screenshot step through the real wiring: a <see cref="SessionRuntime"/> with the app's session
/// policy and a real <see cref="ApprovalServices"/> drives scripted model turns that call the pull request tools.
/// <c>pr_screenshots</c> runs a command from the repository, so its side effect is <see cref="ToolSideEffect.Execute"/> and
/// it always asks: the capture does not start while the card is open and never runs after Deny. <c>pr_edit</c> is the
/// Write half of the same step and asks under <see cref="ToolSideEffect.Write"/>; denying it leaves the description alone.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitScreenshotApprovalTests
{
    private const string ScreenshotsArguments = "{}";
    private const string EditArguments = """{"title":"Approved title"}""";

    [Fact]
    public void The_app_policy_asks_before_pr_screenshots_with_an_execute_side_effect()
    {
        var policy = ConfiguredPolicy();

        Assert.Equal(
            ApprovalMode.Ask,
            policy.Evaluate(new ApprovalRequest(ScreenshotTools.ScreenshotsName, ScreenshotsArguments, ToolSideEffect.Execute, WorkDirectory())));
        Assert.Equal(
            ApprovalMode.Ask,
            policy.Evaluate(new ApprovalRequest(PrTools.EditName, EditArguments, ToolSideEffect.Write, WorkDirectory())));
    }

    [Fact]
    public async Task Approving_the_pr_screenshots_card_runs_the_capture_and_publishes_the_images()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        ScreenshotTestRig.DeclareScreenshots(sandbox, work);
        ScreenshotTestRig.PointOriginAtGitHub(sandbox, work);
        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await RunScriptedTurnAsync(
            session,
            work,
            platform,
            ScriptedScreenshotProvider.Call("call-1", ScreenshotTools.ScreenshotsName, ScreenshotsArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(ScreenshotTools.ScreenshotsName, card.ToolName);
                Assert.Equal(Path.GetFullPath(work), Path.GetFullPath(card.WorkingDirectory), StringComparer.OrdinalIgnoreCase);

                // Nothing runs while the card is open: no capture, no push, no edit.
                Assert.DoesNotContain(events, static e => e is AgentToolFinished);
                Assert.Null(OriginTip(sandbox, "assets/main"));
                Assert.Empty(platform.Edits);
                Assert.Equal(1, await queue.CountAsync());

                card.AllowCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(60));

                var finished = Assert.Single(events.OfType<AgentToolFinished>());
                Assert.Equal(ScreenshotTools.ScreenshotsName, finished.Call.Name);
                Assert.False(finished.Result.IsError, finished.Result.Content);
                Assert.Contains("Pushed 1 screen(s)", finished.Result.Content, StringComparison.Ordinal);
                Assert.NotNull(OriginTip(sandbox, "assets/main"));
                Assert.Contains("## Screenshots", platform.Current!.Body, StringComparison.Ordinal);
                Assert.Single(platform.Edits);
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task Denying_the_pr_screenshots_card_captures_and_publishes_nothing()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        ScreenshotTestRig.DeclareScreenshots(sandbox, work);
        ScreenshotTestRig.PointOriginAtGitHub(sandbox, work);
        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        var bodyBefore = platform.Current.Body;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await RunScriptedTurnAsync(
            session,
            work,
            platform,
            ScriptedScreenshotProvider.Call("call-1", ScreenshotTools.ScreenshotsName, ScreenshotsArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(ScreenshotTools.ScreenshotsName, card.ToolName);

                card.DenyCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                var denied = Assert.Single(events.OfType<AgentApprovalDenied>());
                Assert.Equal(ScreenshotTools.ScreenshotsName, denied.Call.Name);
                Assert.DoesNotContain(events, static e => e is AgentToolFinished { Result.IsError: false });
                Assert.Null(OriginTip(sandbox, "assets/main"));
                Assert.Empty(platform.Edits);
                Assert.Equal(bodyBefore, platform.Current!.Body);
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task Denying_the_pr_edit_card_leaves_the_description_alone()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        var bodyBefore = platform.Current.Body;
        var titleBefore = platform.Current.Title;

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await RunScriptedTurnAsync(
            session,
            work,
            platform,
            ScriptedScreenshotProvider.Call("call-1", PrTools.EditName, EditArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(PrTools.EditName, card.ToolName);

                card.DenyCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Single(events.OfType<AgentApprovalDenied>());
                Assert.Empty(platform.Edits);
                Assert.Equal(titleBefore, platform.Current!.Title);
                Assert.Equal(bodyBefore, platform.Current!.Body);
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task Approving_the_pr_edit_card_writes_the_new_title()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        await RunScriptedTurnAsync(
            session,
            work,
            platform,
            ScriptedScreenshotProvider.Call("call-1", PrTools.EditName, EditArguments),
            async (turn, queue, events) =>
            {
                var card = await turn.NextCardAsync();
                Assert.Equal(PrTools.EditName, card.ToolName);

                card.AllowCommand.Execute(null);
                await turn.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                var finished = Assert.Single(events.OfType<AgentToolFinished>());
                Assert.False(finished.Result.IsError, finished.Result.Content);
                Assert.Equal("Approved title", platform.Current!.Title);
                Assert.Single(platform.Edits);
                Assert.Equal(0, await queue.CountAsync());
            });
    }

    [Fact]
    public async Task The_composition_root_hands_pr_screenshots_to_the_session_runtimes()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-screenshot-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "workspace"));
        var saved = (
            Data: Environment.GetEnvironmentVariable(AppPaths.DataVariable),
            General: Environment.GetEnvironmentVariable(AppPaths.GeneralVariable),
            Workspace: Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable));
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, Path.Combine(root, "general"));
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Path.Combine(root, "workspace"));
        try
        {
            await using var composition = AppComposition.Create();

            // The runtime services are private to the runtime manager; this is the one reflection hop, and the thing under
            // test is what the composition root registered there.
            var field = typeof(AgentRuntimeManager).GetField("services", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("AgentRuntimeManager.services is gone; update this test.");
            var services = (AgentRuntimeServices)(field.GetValue(composition.Runtimes)
                ?? throw new InvalidOperationException("no runtime services"));

            var tool = Assert.Single(services.PullRequestTools, static tool => tool.Definition.Name == ScreenshotTools.ScreenshotsName);
            Assert.Equal(ToolSideEffect.Execute, tool.SideEffect);
            Assert.Contains(services.PullRequestTools, static tool => tool.Definition.Name == PrTools.EditName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.DataVariable, saved.Data);
            Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, saved.General);
            Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, saved.Workspace);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
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

        public Task<ToolApprovalCardViewModel> NextCardAsync() => card.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Runs one scripted turn with the real approval wiring, a real session store and the git and pull request tools
    /// registered the way the composition root registers them.
    /// </summary>
    private static async Task RunScriptedTurnAsync(
        HeadlessUnitTestSession session,
        string work,
        ScreenshotFakePlatform platform,
        IReadOnlyList<ChatStreamEvent> firstResponse,
        Func<TurnHandle, ApprovalQueue, ConcurrentQueue<AgentEvent>, Task> body)
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
                        var info = await store.CreateSessionAsync(work, "screenshot approval e2e", CancellationToken.None);
                        var provider = new ScriptedScreenshotProvider(firstResponse);
                        var git = new GitCli();
                        var settings = new GitToolSettings();
                        var runtimeServices = new AgentRuntimeServices(
                            _ => provider,
                            null,
                            store,
                            store,
                            todos,
                            new ModelSettings("test"),
                            Git: new GitTools(git, settings, new GitReviewLedger()))
                        {
                            Approvals = services,
                            PullRequestTools =
                            [
                                .. new PrTools(git, platform, settings, new GitReviewLedger(), todos).All,
                                .. new ScreenshotTools(git, platform, settings).All,
                            ],
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
                            await completion.WaitAsync(TimeSpan.FromSeconds(60));
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

    /// <summary>The current tip of a branch on the sandbox's origin, or null when the branch is not there.</summary>
    private static string? OriginTip(GitTestSandbox sandbox, string branch)
    {
        try
        {
            return GitTestShell.RunChecked(sandbox.Origin, "rev-parse", "--verify", "refs/heads/" + branch).Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The policy <see cref="SessionRuntime.Build"/> configures into its host: it is a private static (<c>SessionPolicy</c>),
    /// so this fetches the instance the app runs with instead of rebuilding a copy of it.
    /// </summary>
    private static IApprovalPolicy ConfiguredPolicy()
    {
        var field = typeof(SessionRuntime).GetField("SessionPolicy", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "SessionRuntime.SessionPolicy is missing; the approval contract moved. Update this test.");
        return (IApprovalPolicy)(field.GetValue(null) ?? throw new InvalidOperationException("SessionPolicy was null."));
    }

    private static string WorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-screenshot-approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Turn 0 answers with the scripted tool call; every later turn closes the conversation.</summary>
    private sealed class ScriptedScreenshotProvider(IReadOnlyList<ChatStreamEvent> first) : IChatProvider
    {
        private int calls;

        public string Id => "scripted-screenshot";

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
