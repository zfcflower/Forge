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
/// placeholder title before the model is asked anything, <c>set_title</c> replaces it with the model's own words, and a name
/// a person typed is never replaced again - the model is told "kept" instead, which is what stops it trying.
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
        Assert.Contains("Chat title kept: this chat is already named.", results);
        Assert.Equal(2, provider.Requests);
    }

    [Fact]
    public async Task A_still_unnamed_session_tells_its_model_so_for_two_requests_and_never_again()
    {
        Directory.CreateDirectory(directory);
        var (store, runtime, _) = await Session();
        using var http = new HttpClient();
        var view = new SessionWorkspaceViewModel(
            runtime.Session,
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
        Assert.NotNull(runtime.ContextBudget);

        // What the model reads on the requests of an unnamed session: the derived placeholder is called one, and the cap keeps
        // an ignoring model from paying for the line forever.
        var first = runtime.ContextBudget("root");
        Assert.Contains("Session title: this chat is still unnamed", first, StringComparison.Ordinal);
        Assert.Contains("Call set_title once", first, StringComparison.Ordinal);

        // An agent-session tab of the same root shares this runtime; built after the root's own tab, it must not decide what
        // the root's requests say.
        using var http2 = new HttpClient();
        _ = new SessionWorkspaceViewModel(
            new SessionInfo("child-session", directory, "Worker 1", DateTimeOffset.UnixEpoch) { ParentSessionId = runtime.SessionId },
            runtime,
            checkpoints: null,
            store,
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http2),
            agentSession: new AgentSessionLaunch(runtime.Session, 1, IsDraft: true));
        Assert.Contains("Session title", runtime.ContextBudget("root"), StringComparison.Ordinal);

        // The second request is the last one that carries it; a sub-agent's request is not the session's naming moment.
        Assert.DoesNotContain("Session title", runtime.ContextBudget("worker-1"), StringComparison.Ordinal);
        Assert.DoesNotContain("Session title", runtime.ContextBudget("root"), StringComparison.Ordinal);

        // Once the session has a real name the line is gone on the very next request, however early that comes.
        view.Rename(AgentTitle, SessionTitleSource.Agent);
        Assert.DoesNotContain("Session title", runtime.ContextBudget("root"), StringComparison.Ordinal);
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
            return new ValueTask<bool>(true);
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
    private async Task<(ISessionStore Store, SessionRuntime Runtime, ScriptedProvider Provider)> Session(params Script[] scripts)
    {
        var store = new SqliteSessionStore(directory);
        var created = await ((ISessionCatalog)store).CreateAsync(
            new SessionCreate(directory, CreationDefault),
            CancellationToken.None);
        Assert.Null(created.TitleSource);

        var provider = new ScriptedProvider(scripts);
        var runtime = new SessionRuntime(
            new AgentRuntimeServices(
                ProviderFactory: _ => provider,
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            created);
        return (store, runtime, provider);
    }

    private static async Task<List<AgentEvent>> RunTurnAsync(SessionRuntime runtime)
    {
        var events = new List<AgentEvent>();
        await runtime.RunTurnAsync(FirstMessage, events.Add, CancellationToken.None);
        Assert.Contains(events, e => e is AgentTurnCompleted);
        return events;
    }

    private sealed record Script(string? CallName = null, string? ArgumentsJson = null);

    /// <summary>Answers one script entry per request: a tool call, or the end of the turn.</summary>
    private sealed class ScriptedProvider(params Script[] scripts) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public int Requests => calls;

        public List<IReadOnlyList<ChatMessage>> Captured { get; } = [];

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
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
