using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the restart path of a finished sub-agent (<see cref="SessionWorkspaceViewModel.OpenFinished"/> together with
/// <c>SubAgentViewModel.FromStored</c>, forge-app spec "子 Agent 区"): the tab of a child that only the store knows about
/// replays the stored conversation, says so when the store holds nothing, and claims no result the store cannot prove.
///
/// The child sessions come from the session catalog and the messages from the session store, both test doubles; the
/// replay reads really run (they are pool-thread reads) so the test pumps the dispatcher instead of peeking.
/// </summary>
[Collection("forge-app-ui")]
public sealed class FinishedSubAgentReplayTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string ParentSessionId = "main-session";
    private const string ChildSessionId = "child-session";
    private const string ChildTask = "review the diff";
    private const string ChildKind = "Explore";
    private const string UserText = "review the diff";
    private const string AnswerText = "the diff looks good";

    [Fact]
    public async Task A_restarted_session_lists_its_finished_sub_agents()
    {
        var outcome = await RestoreAsync(StoredConversation());

        var agent = Assert.Single(outcome.Finished);
        Assert.Equal(ChildSessionId, agent.ChildSessionId);
        Assert.Equal(ChildKind, agent.Kind);
        Assert.Equal(ChildTask, agent.Task);
        Assert.True(agent.IsReadOnly);
    }

    [Fact]
    public async Task Opening_a_finished_sub_agent_replays_the_stored_conversation()
    {
        var outcome = await RestoreAsync(StoredConversation());

        var texts = outcome.OpenedTranscriptTexts;

        Assert.Contains(UserText, texts);
        Assert.Contains(AnswerText, texts);
    }

    [Fact]
    public async Task Opening_a_finished_sub_agent_without_stored_messages_says_there_is_nothing_to_replay()
    {
        var outcome = await RestoreAsync([]);

        Assert.Contains(SessionStrings.FinishedAgentsNoTranscript.Resolve(), outcome.OpenedNotices);
    }

    /// <summary>A store that cannot prove how the child ended must not turn that into "completed".</summary>
    [Fact]
    public async Task A_finished_sub_agent_without_a_conclusive_tail_only_says_that_it_ended()
    {
        var outcome = await RestoreAsync([Stored(1, ChatMessage.User(UserText))]);

        var agent = Assert.Single(outcome.Finished);
        Assert.Equal(SessionStrings.SubAgentEnded, agent.StatusText);
    }

    private static List<StoredMessage> StoredConversation() =>
    [
        Stored(1, ChatMessage.User(UserText)),
        Stored(2, ChatMessage.Assistant(AnswerText)),
    ];

    private static StoredMessage Stored(long seq, ChatMessage message) =>
        new(seq, message, null, new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(seq));

    private sealed record Outcome(
        IReadOnlyList<SubAgentViewModel> Finished,
        IReadOnlyList<string> OpenedTranscriptTexts,
        IReadOnlyList<string> OpenedNotices);

    /// <summary>Loads the history of a session that already has one stored child session, then opens that child's tab.</summary>
    private static async Task<Outcome> RestoreAsync(IReadOnlyList<StoredMessage> childMessages)
    {
        var session = SharedUi.Session;
        return await session.Dispatch(
            async () =>
            {
                var store = new ChildStore(childMessages);
                var catalog = new ChildCatalog();
                var workspace = new SessionWorkspaceViewModel(
                    Info(ParentSessionId, null, null),
                    new SessionRuntime(
                        new AgentRuntimeServices(
                            static _ => UnconfiguredChatProvider.Instance,
                            HostClient: null,
                            store,
                            catalog,
                            new EmptyTodoStore(),
                            new ModelSettings("model")),
                        Info(ParentSessionId, null, null)),
                    checkpoints: null,
                    store,
                    new ProviderSettings(null, "no provider configured"));

                await workspace.LoadHistoryAsync(CancellationToken.None);
                var agent = Assert.Single(workspace.Finished);
                workspace.OpenFinished(agent);
                Assert.True(await PumpUntilAsync(() => !agent.Transcript.IsEmpty), "the finished sub-agent never replayed anything");

                return new Outcome(
                    [.. workspace.Finished],
                    [.. Texts(agent.Transcript)],
                    [.. agent.Transcript.Items.OfType<NoticeViewModel>().Select(static notice => notice.Text)]);
            },
            CancellationToken.None);
    }

    private static IEnumerable<string> Texts(TranscriptViewModel transcript)
    {
        foreach (var item in transcript.Items)
        {
            switch (item)
            {
                case UserMessageViewModel user:
                    yield return user.Text;
                    break;
                case TurnCardViewModel card:
                    foreach (var segment in card.Blocks.OfType<TurnTextViewModel>().SelectMany(static text => text.Segments))
                    {
                        yield return segment.Text;
                    }

                    break;
            }
        }
    }

    private static SessionInfo Info(string id, string? agentKind, string? task) =>
        new(id, Path.GetTempPath(), id, new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero))
        {
            AgentKind = agentKind,
            Task = task,
            ParentSessionId = string.Equals(id, ChildSessionId, StringComparison.Ordinal) ? ParentSessionId : null,
        };

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

    /// <summary>Only the child session's message log matters; anything else the workspace reads is empty.</summary>
    private sealed class ChildStore(IReadOnlyList<StoredMessage> childMessages) : ISessionStore
    {
        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) =>
            Task.FromResult(Info(ChildSessionId, ChildKind, ChildTask));

        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<SessionInfo?>(Info(sessionId, null, null));

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) =>
            Task.FromResult(1L);

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) =>
            ReadCoreAsync(sessionId, fromSeq, toSeq);

        private async IAsyncEnumerable<StoredMessage> ReadCoreAsync(string sessionId, long fromSeq, long? toSeq)
        {
            if (string.Equals(sessionId, ChildSessionId, StringComparison.Ordinal))
            {
                foreach (var message in childMessages)
                {
                    if (message.Seq >= fromSeq && (toSeq is null || message.Seq <= toSeq))
                    {
                        yield return message;
                    }
                }
            }

            await Task.Yield();
        }

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(string.Equals(sessionId, ChildSessionId, StringComparison.Ordinal) ? (long)childMessages.Count : 0L);

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SummaryRecord>>([]);

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([]);
    }

    /// <summary>One child session of <see cref="ParentSessionId"/>, as the catalog returns it after a restart.</summary>
    private sealed class ChildCatalog : ISessionCatalog
    {
        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(Info(ChildSessionId, ChildKind, ChildTask));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>(
                string.Equals(query.ParentSessionId, ParentSessionId, StringComparison.Ordinal)
                    ? [Info(ChildSessionId, ChildKind, ChildTask)]
                    : []);

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyTodoStore : ITodoStore
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
