using System.Data.Common;
using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
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
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
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
        Assert.Contains(ScriptedProvider.Answer, text.Segments.Select(static segment => segment.Text));
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

            await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
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

                    var store = new FakeSessionStore();
                    var catalog = new FakeSessionCatalog();
                    var runtime = new SessionRuntime(
                        new AgentRuntimeServices(
                            static _ => new ScriptedProvider(),
                            HostClient: null,
                            store,
                            catalog,
                            new FakeTodoStore(),
                            new ModelSettings("scripted-model")),
                        Info(repository));
                    var workspace = new SessionWorkspaceViewModel(
                        Info(repository),
                        runtime,
                        new TurnCheckpointService(git, checkpoints),
                        store,
                        new ProviderSettings(null, "no provider configured"));

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
            DeleteTree(root);
        }
    }

    private static SessionInfo Info(string workingDirectory) =>
        new(SessionId, workingDirectory, "demo session", new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-turn-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        return condition();
    }

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

    /// <summary>In-memory message log; the checkpoints own the assertions, so the log only has to behave like a store.</summary>
    private sealed class FakeSessionStore : ISessionStore, ISessionCatalog
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, List<StoredMessage>> log = new(StringComparer.Ordinal);

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", workspacePath, title, DateTimeOffset.UnixEpoch));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct)
        {
            lock (gate)
            {
                if (!log.TryGetValue(sessionId, out var messages))
                {
                    messages = [];
                    log[sessionId] = messages;
                }

                messages.Add(new StoredMessage(messages.Count + 1, message, usage, DateTimeOffset.UnixEpoch));
                return Task.FromResult((long)messages.Count);
            }
        }

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            ReadCoreAsync(sessionId, fromSeq, toSeq);

        private async IAsyncEnumerable<StoredMessage> ReadCoreAsync(string sessionId, long fromSeq, long? toSeq)
        {
            List<StoredMessage> snapshot;
            lock (gate)
            {
                snapshot = log.TryGetValue(sessionId, out var messages) ? [.. messages] : [];
            }

            foreach (var message in snapshot)
            {
                if (message.Seq >= fromSeq && (toSeq is null || message.Seq <= toSeq))
                {
                    yield return message;
                }
            }

            await Task.Yield();
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct)
        {
            lock (gate)
            {
                return Task.FromResult(log.TryGetValue(sessionId, out var messages) ? (long)messages.Count : 0L);
            }
        }

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSessionCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeTodoStore : ITodoStore
    {
        public event Action<string>? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TodoItem>>([]);

        public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
            Task.FromResult(new TodoItem("t1", sessionId, title, TodoStatus.Pending, note, 0, DateTimeOffset.UnixEpoch));

        public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
            Task.FromResult<TodoItem?>(null);

        public Task<bool> RemoveAsync(string id, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => Task.FromResult(false);
    }
}
