using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The session's name, end to end: a real store, a real runtime and one scripted model. The first message writes the
/// placeholder title before the model is asked anything, <c>set_title</c> replaces it with the model's own words, and the
/// name is settled with the turn that starts from that message: a person's name, the model's own, and a later turn of a
/// session reopened from history all come back "kept", which is what stops the model from trying again.
/// </summary>
public sealed class SessionTitleNamingTests : IDisposable
{
    private const string FirstMessage = "修复登录跳转：src/Auth/Login.cs 里的重定向写错了";
    private const string DerivedTitle = "修复登录跳转 里的重定向写错了";
    private const string AgentTitle = "修复登录跳转";
    private const string CreationDefault = "会话 11-21 14:00";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-title-" + Guid.NewGuid().ToString("N"));

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
    public async Task First_message_names_the_session_and_the_model_replaces_the_placeholder()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script("set_title", """{"title":"修复登录跳转"}"""), new Script());
        var named = new List<string>();
        runtime.TitleNamed += (title, source) => named.Add(source + ":" + title);

        await RunTurnAsync(runtime);

        // Two writes, in this order: the placeholder the host derived, then the model's name over it.
        Assert.Equal([$"derived:{DerivedTitle}", $"agent:{AgentTitle}"], named);
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(AgentTitle, row.Title);
        Assert.Equal(SessionTitleSource.Agent, row.TitleSource);
    }

    [Fact]
    public async Task A_name_a_person_typed_is_kept_and_the_model_is_told_so()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, provider) = await Session(new Script("set_title", """{"title":"修复登录跳转"}"""), new Script());
        var renamed = await ((ISessionCatalog)store).RenameAsync(runtime.SessionId, "我的叫法", CancellationToken.None);
        Assert.True(renamed);
        var named = new List<string>();
        runtime.TitleNamed += (title, source) => named.Add(source + ":" + title);

        var events = await RunTurnAsync(runtime);

        // Nothing was named: no placeholder over a person's title, and the model's own attempt was refused.
        Assert.Empty(named);
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("我的叫法", row.Title);
        Assert.Equal(SessionTitleSource.User, row.TitleSource);

        // The refusal reached the model as this turn's tool result, which is what keeps it from asking again.
        var results = events.OfType<AgentToolFinished>().Select(finished => finished.Result.Text).ToList();
        Assert.Contains("Chat title kept: this chat's name is already settled.", results);
        Assert.Equal(2, provider.Requests);
    }

    [Fact]
    public async Task A_session_gets_one_naming_turn_and_a_refusal_after_it()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, provider) = await Session(
            new Script(),
            new Script("set_title", """{"title":"修复登录跳转"}"""),
            new Script());
        using var http = new HttpClient();
        _ = Tab(store, runtime, http);

        // The first turn carries the line that asks for a name, and this model lets that one go by.
        await RunTurnAsync(runtime);
        Assert.Contains("Session title: this chat is still unnamed", Suffix(provider.Captured[0]), StringComparison.Ordinal);
        var afterFirst = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(afterFirst);
        Assert.Equal(DerivedTitle, afterFirst.Title);

        // The second turn brings neither the line nor a writable title: the model's own name is refused and the placeholder stays.
        var events = await RunTurnAsync(runtime, "再帮我看看这个重定向");
        Assert.DoesNotContain("Session title", Suffix(provider.Captured[1]), StringComparison.Ordinal);
        var afterSecond = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(afterSecond);
        Assert.Equal(DerivedTitle, afterSecond.Title);
        Assert.Equal(SessionTitleSource.Derived, afterSecond.TitleSource);
        Assert.Contains(
            "Chat title kept: this chat's name is already settled.",
            events.OfType<AgentToolFinished>().Select(finished => finished.Result.Text));
    }

    [Fact]
    public async Task A_reopened_session_keeps_the_name_it_settled_on()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script());
        using var http = new HttpClient();
        _ = Tab(store, runtime, http);
        await RunTurnAsync(runtime);

        // The window is a fact of the store, not of the view that opened it: a session reopened from history already holds a
        // message, so the turn it comes back on brings no reminder and cannot rename it either.
        var stored = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(stored);
        var provider = new ScriptedProvider(new Script("set_title", """{"title":"重开后的新名字"}"""), new Script());
        var reopened = Runtime(store, stored, provider);
        using var reopenedHttp = new HttpClient();
        _ = Tab(store, reopened, reopenedHttp);

        var events = await RunTurnAsync(reopened, "关掉又打开的会话");

        Assert.DoesNotContain("Session title", Suffix(provider.Captured[0]), StringComparison.Ordinal);
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(DerivedTitle, row.Title);
        Assert.Equal(SessionTitleSource.Derived, row.TitleSource);
        Assert.Contains(
            "Chat title kept: this chat's name is already settled.",
            events.OfType<AgentToolFinished>().Select(finished => finished.Result.Text));
    }

    [Fact]
    public async Task A_first_turn_that_wrote_nothing_leaves_the_naming_window_open()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script("set_title", """{"title":"修复登录跳转"}"""), new Script());

        // Cancelled before its user message reached the store: the session has not started, so its next turn is still its
        // first one and the model's name lands there.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RunTurnAsync(FirstMessage, [], _ => { }, new CancellationToken(canceled: true)));
        Assert.Equal(0, await ((ISessionStore)store).LastSeqAsync(runtime.SessionId, CancellationToken.None));

        await RunTurnAsync(runtime);

        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(AgentTitle, row.Title);
        Assert.Equal(SessionTitleSource.Agent, row.TitleSource);
    }

    [Fact]
    public async Task The_naming_line_rides_two_requests_of_its_turn_and_no_sub_agent_request()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, provider) = await Session(
            new Script("set_title", """{"title":""}"""),
            new Script("set_title", """{"title":""}"""),
            new Script());
        using var http = new HttpClient();
        _ = Tab(store, runtime, http);
        provider.SuffixOnFirstRequest = runtime.ContextBudget!;

        await RunTurnAsync(runtime);

        // Three requests in one turn (the two nameless set_title calls write nothing): the line stops after the second, so an
        // ignoring model costs a fixed amount; and a sub-agent of that same turn is never told to name anything.
        Assert.Contains("Session title", Suffix(provider.Captured[0]), StringComparison.Ordinal);
        Assert.Contains("Session title", Suffix(provider.Captured[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("Session title", Suffix(provider.Captured[2]), StringComparison.Ordinal);
        Assert.Null(provider.SubAgentSuffix);
    }

    [Fact]
    public async Task A_long_name_from_the_model_is_cut_to_one_tab_label()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(
            new Script("set_title", """{"title":"目前会话的命名出现了大段文本变成命名的情况；且没有约束到只有首轮才能命名"}"""),
            new Script());

        var events = await RunTurnAsync(runtime);

        // The model answered with a sentence; the session keeps its first clause, and the model is told exactly what the tab
        // now shows rather than the sentence it asked for.
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("目前会话的命名出现了大段文本变成命名的情况", row.Title);
        Assert.Equal(SessionTitleSource.Agent, row.TitleSource);
        Assert.Contains(
            "Chat title set to \"目前会话的命名出现了大段文本变成命名的情况\".",
            events.OfType<AgentToolFinished>().Select(finished => finished.Result.Text));
    }

    [Fact]
    public async Task A_first_message_that_is_a_paragraph_derives_only_its_first_clause()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script());

        await RunTurnAsync(runtime, "目前会话的命名出现了大段文本变成命名的情况；且没有约束到只有首轮才能命名");

        // The placeholder is a label, not the opening of the request: a long first message keeps its first clause and drops
        // the rest, so a tab never reads as a paragraph.
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("目前会话的命名出现了大段文本变成命名的情况", row.Title);
        Assert.Equal(SessionTitleSource.Derived, row.TitleSource);
    }

    [Fact]
    public async Task An_agent_session_tab_leaves_the_root_requests_alone()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, provider) = await Session(new Script());
        using var http = new HttpClient();
        _ = Tab(store, runtime, http);

        // An agent-session tab of the same root shares this runtime; built after the root's own tab, it must not decide what
        // the root's requests say - the naming line is the root's own.
        using var childHttp = new HttpClient();
        _ = new SessionWorkspaceViewModel(
            new SessionInfo("child-session", directory, "Worker 1", DateTimeOffset.UnixEpoch) { ParentSessionId = runtime.SessionId },
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, childHttp),
            agentSession: new AgentSessionLaunch(runtime.Session, 1, IsDraft: true, Forge.Core.Prompts.BuiltInAgents.Worker));

        await RunTurnAsync(runtime);

        Assert.Contains("Session title", Suffix(provider.Captured[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_automation_turn_cannot_be_named_by_its_model()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script("set_title", """{"title":"Nightly triage"}"""), new Script());

        await runtime.RunTurnAsync(FirstMessage, [], _ => { }, CancellationToken.None, deriveTitle: false);

        // The automation named this session when it created it, so its turn never opens the window - the name stays.
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(CreationDefault, row.Title);
        Assert.Null(row.TitleSource);
    }

    [Fact]
    public async Task A_direct_orchestration_message_is_named_after_the_request_not_the_directive()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script());
        var message = OrchestrationDirective.Apply(OrchestrationChoice.Direct, FirstMessage);

        await runtime.RunTurnAsync(message, [], _ => { }, CancellationToken.None);

        // The composer wraps the request in an instruction block; the label has to read as the person's own words.
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(DerivedTitle, row.Title);
        Assert.Equal(SessionTitleSource.Derived, row.TitleSource);
    }

    [Fact]
    public async Task An_automation_turn_leaves_the_name_the_session_already_has()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session(new Script());
        var named = new List<string>();
        runtime.TitleNamed += (title, source) => named.Add(source + ":" + title);

        await runtime.RunTurnAsync(FirstMessage, [], _ => { }, CancellationToken.None, deriveTitle: false);

        Assert.Empty(named);
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(CreationDefault, row.Title);
        Assert.Null(row.TitleSource);
    }

    [Fact]
    public async Task A_store_write_refuses_a_source_the_reader_would_not_understand()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session();

        // "user" is the dialog's own path and a typo would leave this session unreadable for good, so the write refuses both
        // before it reaches SQL - and the row is untouched afterwards.
        await Assert.ThrowsAsync<ArgumentException>(
            () => ((ISessionCatalog)store).TryTitleAsync(runtime.SessionId, "名字", SessionTitleSource.User, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ((ISessionCatalog)store).TryTitleAsync(runtime.SessionId, "名字", "derivedd", CancellationToken.None));

        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(CreationDefault, row.Title);
        Assert.Null(row.TitleSource);
    }

    [Fact]
    public async Task A_sub_agent_cannot_name_the_conversation_it_was_spawned_into()
    {
        var calls = 0;
        var tool = new Forge.Core.Agents.SetTitleTool((_, _) =>
        {
            calls++;
            return new ValueTask<string?>("子 Agent 的名字");
        });

        var result = await tool.InvokeAsync(
            new ToolCall("c1", "set_title", """{"title":"子 Agent 的名字"}"""),
            new ToolContext("child-session", "wd", AgentDepth: 1),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("only the root agent names this conversation", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    /// <summary>A session whose stored row still carries the creation default, the way the "+" button leaves it.</summary>
    private async Task<(SqliteSessionStore Store, SessionRuntime Runtime, ScriptedProvider Provider)> Session(params Script[] scripts)
    {
        var store = new SqliteSessionStore(directory);
        var created = await ((ISessionCatalog)store).CreateAsync(
            new SessionCreate(directory, CreationDefault),
            CancellationToken.None);
        Assert.Null(created.TitleSource);

        var provider = new ScriptedProvider(scripts);
        return (store, Runtime(store, created, provider), provider);
    }

    private SessionRuntime Runtime(SqliteSessionStore store, SessionInfo session, IChatProvider provider) =>
        new(
            new AgentRuntimeServices(
                ProviderFactory: _ => provider,
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            session);

    /// <summary>The tab that installs this session's per-request suffix (budget and naming lines) on the runtime.</summary>
    private SessionWorkspaceViewModel Tab(ISessionStore store, SessionRuntime runtime, HttpClient http) =>
        new(
            runtime.Session,
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));

    private static async Task<List<AgentEvent>> RunTurnAsync(SessionRuntime runtime, string text = FirstMessage)
    {
        var events = new List<AgentEvent>();
        await runtime.RunTurnAsync(text, events.Add, CancellationToken.None);
        Assert.Contains(events, e => e is AgentTurnCompleted);
        return events;
    }

    /// <summary>The trailing dynamic suffix of a request - the last user message, which is where the naming line rides.</summary>
    private static string Suffix(IReadOnlyList<ChatMessage> messages) =>
        messages[^1].Content.OfType<TextBlock>().Aggregate(string.Empty, (text, block) => text + block.Text);

    private sealed record Script(string? CallName = null, string? ArgumentsJson = null);

    /// <summary>Answers one script entry per request: a tool call, or the end of the turn.</summary>
    private sealed class ScriptedProvider(params Script[] scripts) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public int Requests => calls;

        public List<IReadOnlyList<ChatMessage>> Captured { get; } = [];

        /// <summary>Reads the session's installed per-request suffix for a sub-agent on the first request; asking about the
        /// root would spend the naming cap the assertions are about.</summary>
        public Func<string, string?>? SuffixOnFirstRequest { get; set; }

        public string? SubAgentSuffix { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (calls == 0 && SuffixOnFirstRequest is { } suffix)
            {
                SubAgentSuffix = suffix("worker-1");
            }

            Captured.Add([.. request.Messages]);
            var script = scripts[calls];
            var index = ++calls;
            if (script.CallName is { } name)
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{index}", name, script.ArgumentsJson ?? "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(ChatMessage.Assistant("done"), TokenUsage.Empty, StopReason.EndTurn);
            }
        }
    }
}

/// <summary>
/// The automation path through the view, which is where the decision is really made: an automation turn must neither write a
/// placeholder over the automation's own name nor tell the model the session is unnamed. Only a turn driven through
/// <see cref="SessionWorkspaceViewModel.RunAutomationTurnAsync"/> exercises `automationTurn`, the flag both halves read.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionTitleAutomationTurnTests : IDisposable
{
    private const string CreationDefault = "Nightly triage 11-21 14:00";
    private const string Prompt = "修复登录跳转：src/Auth/Login.cs 里的重定向写错了";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-title-auto-" + Guid.NewGuid().ToString("N"));

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
    public Task An_automation_turn_neither_renames_the_session_nor_asks_the_model_to() => SharedUi.RunAsync(async () =>
    {
        Directory.CreateDirectory(directory);
        var store = new SqliteSessionStore(directory);
        var created = await ((ISessionCatalog)store).CreateAsync(new SessionCreate(directory, CreationDefault), CancellationToken.None);
        var provider = new EndTurnProvider();
        var runtime = new SessionRuntime(
            new AgentRuntimeServices(
                ProviderFactory: _ => provider,
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            created);
        using var http = new HttpClient();
        var view = new SessionWorkspaceViewModel(
            created,
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
        var named = new List<string>();
        runtime.TitleNamed += (title, source) => named.Add(source + ":" + title);

        var result = await view.RunAutomationTurnAsync(Prompt, LocText.Literal("Nightly triage"), agent: null);

        Assert.Equal(MailboxStatus.Completed, result.Status);
        Assert.Empty(named);
        var row = await store.GetSessionAsync(runtime.SessionId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(CreationDefault, row.Title);
        Assert.Null(row.TitleSource);

        // The naming line rides the last user message: the automation turn must not carry it, or the model would rename a
        // session the automation named.
        var sent = Assert.Single(provider.Captured);
        Assert.DoesNotContain("Session title", sent[^1].Content.OfType<TextBlock>().Aggregate(string.Empty, (text, b) => text + b.Text), StringComparison.Ordinal);
    });

    /// <summary>Answers every request by ending the turn - the model never calls a tool here.</summary>
    private sealed class EndTurnProvider : IChatProvider
    {
        public string Id => "end-turn";

        public List<IReadOnlyList<ChatMessage>> Captured { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Captured.Add([.. request.Messages]);
            yield return new MessageCompleted(ChatMessage.Assistant("done"), TokenUsage.Empty, StopReason.EndTurn);
        }
    }
}
