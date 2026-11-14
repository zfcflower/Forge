using System.Data.Common;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the "a failing checkpoint never leaves the session running" rule of
/// <see cref="SessionWorkspaceViewModel"/>: the next-turn-number read, the begin snapshot and the complete snapshot all
/// sit inside the same try/finally as the turn itself, so a broken checkpoint store leaves <c>IsRunning</c> false,
/// raises <c>TurnCompleted</c> exactly once and says in the transcript what failed.
///
/// The agent turn runs for real (an <see cref="AgentHost"/> over a scripted provider and in-memory stores); only the
/// checkpoint store and git are the things under test, so the failure is the only thing that differs from a good turn.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionTurnFailureTests
{
    private const string SessionId = "session-under-test";
    private const string FailureText = "checkpoint store is down";
    private const string UserText = "please answer";

    [Fact]
    public async Task A_failing_checkpoint_begin_leaves_the_session_idle_and_completes_the_turn_once()
    {
        var outcome = await RunTurnAsync(CheckpointFailure.Begin);

        Assert.False(outcome.IsRunning);
        Assert.Equal(1, outcome.TurnCompleted);
        Assert.Contains(outcome.Notices, IsCheckpointNotice);
    }

    [Fact]
    public async Task A_failing_checkpoint_complete_leaves_the_session_idle_and_completes_the_turn_once()
    {
        var outcome = await RunTurnAsync(CheckpointFailure.Complete);

        Assert.False(outcome.IsRunning);
        Assert.Equal(1, outcome.TurnCompleted);
        Assert.Contains(outcome.Notices, IsCheckpointNotice);
    }

    [Fact]
    public async Task A_failing_next_turn_number_leaves_the_session_idle_and_completes_the_turn_once()
    {
        var outcome = await RunTurnAsync(CheckpointFailure.NextTurnNumber);

        Assert.False(outcome.IsRunning);
        Assert.Equal(1, outcome.TurnCompleted);
        Assert.Contains(outcome.Notices, IsCheckpointNotice);
    }

    /// <summary>The checkpoint failure must not cost the user the answer that was already streaming.</summary>
    [Fact]
    public async Task A_failing_checkpoint_keeps_the_answer_that_streamed()
    {
        var outcome = await RunTurnAsync(CheckpointFailure.Complete);

        var card = Assert.Single(outcome.Transcript.OfType<TurnCardViewModel>());
        var text = Assert.Single(card.Blocks.OfType<TurnTextViewModel>());
        Assert.Contains(ScriptedProvider.Answer, text.CopyText, StringComparison.Ordinal);
    }

    private static bool IsCheckpointNotice(string notice) =>
        notice.StartsWith(SessionStrings.CheckpointFailed(string.Empty).Resolve(), StringComparison.Ordinal) &&
        notice.Contains(FailureText, StringComparison.Ordinal);

    private enum CheckpointFailure
    {
        Begin,
        Complete,
        NextTurnNumber,
    }

    private sealed record Outcome(bool IsRunning, int TurnCompleted, IReadOnlyList<string> Notices, IReadOnlyList<TranscriptItemViewModel> Transcript);

    private static async Task<Outcome> RunTurnAsync(CheckpointFailure failure)
    {
        var root = NewRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var git = new GitCli();
            Assert.True((await git.RunAsync(repository, ["init"], CancellationToken.None)).Success);
            await File.WriteAllTextAsync(Path.Combine(repository, "readme.txt"), "first");

            var session = SharedUi.Session;
            return await session.Dispatch(
                async () =>
                {
                    var checkpoints = new FakeCheckpointStore();
                    switch (failure)
                    {
                        case CheckpointFailure.Begin:
                            checkpoints.BeginFailure = new IOException(FailureText);
                            break;
                        case CheckpointFailure.Complete:
                            checkpoints.CompleteFailure = new FakeDbException(FailureText);
                            break;
                        default:
                            checkpoints.ListFailure = new FakeDbException(FailureText);
                            break;
                    }

                    var store = new MemorySessionStore();
                    var catalog = new EmptySessionCatalog(DateTimeOffset.UnixEpoch);
                    var runtime = new SessionRuntime(
                        new AgentRuntimeServices(
                            static _ => new ScriptedProvider(),
                            HostClient: null,
                            store,
                            catalog,
                            new EmptyTodoStore("t1", DateTimeOffset.UnixEpoch),
                            new ModelSettings("scripted-model")),
                        Info(repository));
                    var workspace = new SessionWorkspaceViewModel(
                        Info(repository),
                        runtime,
                        new TurnCheckpointService(git, checkpoints),
                        store,
                        TestProviderServices.Unconfigured(repository));

                    var completed = 0;
                    workspace.TurnCompleted += _ => completed++;
                    workspace.Draft = UserText;
                    workspace.SendCommand.Execute(null);

                    Assert.True(await PumpUntilAsync(() => completed > 0), "the turn never completed");
                    return new Outcome(
                        workspace.IsRunning,
                        completed,
                        [.. workspace.Transcript.Items.OfType<NoticeViewModel>().Select(static notice => notice.Text)],
                        [.. workspace.Transcript.Items]);
                },
                CancellationToken.None);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    private static SessionInfo Info(string workingDirectory) =>
        new(SessionId, workingDirectory, "demo session", new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>An empty catalogue under the test root (deleted with it); the turn itself runs on the scripted runtime provider.</summary>
    private static string NewRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-turn-failure-");
        return root;
    }

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition);

    /// <summary>One tool-free assistant turn, so the checkpoint failure is the only unusual thing in the run.</summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        public const string Answer = "the answer";

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new TextDelta(Answer);
            yield return new MessageCompleted(ChatMessage.Assistant(Answer), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);

    private sealed class FakeCheckpointStore : ITurnCheckpointStore
    {
        public Exception? BeginFailure { get; set; }

        public Exception? CompleteFailure { get; set; }

        public Exception? ListFailure { get; set; }

        public Task BeginAsync(string sessionId, int turn, string worktreePath, string treeBefore, DateTimeOffset at, CancellationToken ct) =>
            BeginFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;

        public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct) =>
            CompleteFailure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(true);

        public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct) =>
            ListFailure is { } failure
                ? Task.FromException<IReadOnlyList<TurnCheckpoint>>(failure)
                : Task.FromResult<IReadOnlyList<TurnCheckpoint>>([]);
    }

}
