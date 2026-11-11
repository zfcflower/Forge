using System.Collections.Concurrent;
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
/// Pins how <see cref="SessionWorkspaceViewModel.RunAutomationTurnAsync"/> turns a real turn into a mailbox result: the
/// outcome is read from the root agent's own events on the worker thread (not from UI state that the frame coalescer has
/// not applied yet), a spent quota travels with the result instead of starting a second resume run, and an agent given for
/// the turn applies to that turn only.
///
/// The turn runs for real (an <see cref="AgentHost"/> over a scripted provider and in-memory stores).
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionAutomationTurnTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string SessionId = "automation-turn-session";

    [Fact]
    public async Task A_good_turn_completes_and_the_transcript_says_where_it_came_from()
    {
        var outcome = await RunAsync(ProviderMode.Answer, agent: null);

        Assert.Equal(MailboxStatus.Completed, outcome.Result.Status);
        Assert.Null(outcome.Result.Quota);
        Assert.Contains(AutomationStrings.OriginNotice("Nightly").Resolve(), outcome.Notices);
        Assert.Empty(outcome.QuotaEvents);
        Assert.Equal([true], outcome.TurnStarts);
    }

    [Fact]
    public async Task A_spent_quota_comes_back_with_the_result_and_raises_no_quota_event()
    {
        var outcome = await RunAsync(ProviderMode.Quota, agent: null);

        Assert.Equal(MailboxStatus.TurnFailed, outcome.Result.Status);
        var quota = Assert.IsType<ProviderQuotaExhausted>(outcome.Result.Quota);
        Assert.Equal("scripted-model", quota.Model);
        Assert.Empty(outcome.QuotaEvents);
    }

    [Fact]
    public async Task Any_other_provider_failure_is_a_failed_turn_without_quota()
    {
        var outcome = await RunAsync(ProviderMode.Fail, agent: null);

        Assert.Equal(MailboxStatus.TurnFailed, outcome.Result.Status);
        Assert.Null(outcome.Result.Quota);
        Assert.Contains("messages: field required", outcome.Result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agent_given_for_the_turn_runs_that_turn_and_the_session_goes_back_to_its_own_agent()
    {
        var scout = new AgentDefinition("scout", "read-only", "Investigate only.", new ModelSettings("scout-model"), ["read"]);

        var outcome = await RunAsync(ProviderMode.Answer, scout);

        Assert.Equal(MailboxStatus.Completed, outcome.Result.Status);
        Assert.Contains("scout-model", outcome.ModelsSeen);
        Assert.Null(outcome.RootDefinitionAfter);
    }

    private enum ProviderMode
    {
        Answer,
        Quota,
        Fail,
    }

    private sealed record Outcome(
        MailboxResult Result,
        IReadOnlyList<string> Notices,
        IReadOnlyList<ProviderQuotaExhausted> QuotaEvents,
        IReadOnlyList<bool> TurnStarts,
        IReadOnlyList<string> ModelsSeen,
        AgentDefinition? RootDefinitionAfter);

    private static async Task<Outcome> RunAsync(ProviderMode mode, AgentDefinition? agent)
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-automation-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = Path.Combine(root, "repository");
            Directory.CreateDirectory(repository);
            var git = new GitCli();
            Assert.True((await git.RunAsync(repository, ["init"], CancellationToken.None)).Success);
            await File.WriteAllTextAsync(Path.Combine(repository, "readme.txt"), "first");

            return await SharedUi.Session.Dispatch(
                async () =>
                {
                    var provider = new ScriptedProvider(mode);
                    var store = new MemorySessionStore();
                    var runtime = new SessionRuntime(
                        new AgentRuntimeServices(
                            _ => provider,
                            HostClient: null,
                            store,
                            store,
                            new NoTodos(),
                            new ModelSettings("scripted-model")),
                        Info(repository));
                    var workspace = new SessionWorkspaceViewModel(
                        Info(repository),
                        runtime,
                        new TurnCheckpointService(git, new NoCheckpoints()),
                        store,
                        UnconfiguredProviders(root));

                    var quotaEvents = new List<ProviderQuotaExhausted>();
                    var turnStarts = new List<bool>();
                    workspace.QuotaExhausted += (_, quota) => quotaEvents.Add(quota);
                    workspace.TurnStarted += (_, automation) => turnStarts.Add(automation);

                    var result = await workspace.RunAutomationTurnAsync("do the nightly check", AutomationStrings.OriginNotice("Nightly"), agent);

                    // Let the frame coalescer apply whatever was still queued, so a late quota event would show up.
                    await PumpAsync();
                    return new Outcome(
                        result,
                        [.. workspace.Transcript.Items.OfType<NoticeViewModel>().Select(static notice => notice.Text)],
                        quotaEvents,
                        turnStarts,
                        [.. provider.Models],
                        runtime.RootDefinition);
                },
                CancellationToken.None);
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static SessionInfo Info(string workingDirectory) =>
        new(SessionId, workingDirectory, "automation session", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private static ProviderServices UnconfiguredProviders(string root)
    {
        var settingsPath = Path.Combine(root, "providers-settings.json");
        File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
        var dataDir = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(dataDir);
        return new ProviderServices(settingsPath, dataDir, new HttpClient(new NoNetwork()));
    }

    private static async Task PumpAsync()
    {
        for (var i = 0; i < PumpIterations / 10; i++)
        {
            await Task.Delay(PumpDelayMilliseconds);
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private sealed class ScriptedProvider(ProviderMode mode) : IChatProvider
    {
        public ConcurrentQueue<string> Models { get; } = new();

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Models.Enqueue(request.Model.Model);
            await Task.Yield();
            switch (mode)
            {
                case ProviderMode.Quota:
                    throw new ProviderException(
                        """scripted returned 429: {"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}""",
                        retryable: false,
                        statusCode: 429);
                case ProviderMode.Fail:
                    throw new ProviderException("scripted returned 400: messages: field required", retryable: false, statusCode: 400);
            }

            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }

    private sealed class NoCheckpoints : ITurnCheckpointStore
    {
        public Task BeginAsync(string sessionId, int turn, string worktreePath, string treeBefore, DateTimeOffset at, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<bool> CompleteAsync(string sessionId, int turn, string treeAfter, DateTimeOffset at, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<TurnCheckpoint>> ListAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TurnCheckpoint>>([]);
    }

    private sealed class MemorySessionStore : ISessionStore, ISessionCatalog
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

    private sealed class NoTodos : ITodoStore
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
