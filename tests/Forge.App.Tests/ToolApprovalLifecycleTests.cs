using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A call that has to ask the user exists on screen from the moment the loop commits to it. The core half pins the event
/// order on the real loop: the start event carries <c>AwaitingApproval</c> and comes before the gate, a call the gate lets
/// through follows it with <c>AgentToolRunning</c>, a denied one ends on <c>AgentApprovalDenied</c> and never runs. The app
/// half pins what that does to the card: a still clock while it waits, the turning glyph once it runs, and the refusal
/// closing the same card instead of adding a second line about the same call.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ToolApprovalLifecycleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tool-approval-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
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

    // ------------------------------------------------------------------ the loop's event order

    /// <summary>A write-shaped tool: the call asks as soon as its path argument leaves the workspace.</summary>
    private sealed class WritingTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDefinition Definition { get; } = new("probe_write", "write", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.Write;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult("written"));
        }
    }

    /// <summary>A read-only tool: the policy never asks about it.</summary>
    private sealed class ReadingTool : ITool
    {
        public int Calls { get; private set; }

        public ToolDefinition Definition { get; } = new("probe_read", "read", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult("read"));
        }
    }

    /// <summary>Answers the first request with one tool call and every later one with a sentence, the way a provider does.</summary>
    private sealed class OneCallProvider(string tool, string arguments) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var index = Interlocked.Increment(ref calls);
            yield return new MessageCompleted(
                index == 1
                    ? new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c1", tool, arguments)])
                    : new ChatMessage(MessageRole.Assistant, [new Forge.Abstractions.TextBlock("done")]),
                TokenUsage.Empty,
                StopReason.ToolUse);
        }
    }

    /// <summary>Answers every approval request the same way and counts them, so a missing ask shows up as a zero.</summary>
    private sealed class DecidingHandler(ApprovalDecision decision) : IApprovalHandler
    {
        public int Requests { get; private set; }

        public ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Requests++;
            return ValueTask.FromResult(decision);
        }
    }

    private async Task<List<AgentEvent>> RunAsync(ITool tool, IApprovalHandler? handler, string arguments)
    {
        Directory.CreateDirectory(directory);
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => new OneCallProvider(tool.Definition.Name, arguments),
            Tools = [tool],
            Layers = new PromptLayers("sys", null, null),
            ApprovalHandler = handler,
        });
        var root = host.CreateRoot("session-approval", directory);
        var events = new List<AgentEvent>();
        await foreach (var value in host.RunAsync(root, "go", TestContext.Current.CancellationToken))
        {
            events.Add(value);
        }

        return events;
    }

    private static List<string> Lifecycle(List<AgentEvent> events) =>
        events.Where(e => e is AgentToolStarted or AgentToolRunning or AgentToolFinished)
            .Select(e => e.GetType().Name)
            .ToList();

    [Fact]
    public async Task An_allowed_call_starts_waiting_then_runs_then_finishes()
    {
        var tool = new WritingTool();
        var handler = new DecidingHandler(ApprovalDecision.Allow);

        var events = await RunAsync(tool, handler, """{"path":"../outside.txt"}""");

        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(new[] { "AgentToolStarted", "AgentToolRunning", "AgentToolFinished" }, Lifecycle(events));
        Assert.True(Assert.Single(events.OfType<AgentToolStarted>()).AwaitingApproval);
    }

    [Fact]
    public async Task A_denied_call_starts_waiting_and_ends_on_the_denial_without_running()
    {
        var tool = new WritingTool();
        var handler = new DecidingHandler(ApprovalDecision.Deny);

        var events = await RunAsync(tool, handler, """{"path":"../outside.txt"}""");

        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(new[] { "AgentToolStarted" }, Lifecycle(events));
        Assert.True(Assert.Single(events.OfType<AgentToolStarted>()).AwaitingApproval);
        Assert.Single(events.OfType<AgentApprovalDenied>());
        Assert.True(
            events.FindIndex(e => e is AgentToolStarted) < events.FindIndex(e => e is AgentApprovalDenied),
            "the row has to exist before the refusal can close it");
    }

    [Fact]
    public async Task A_call_that_needs_no_answer_runs_without_a_second_event()
    {
        var tool = new ReadingTool();

        var events = await RunAsync(tool, handler: null, """{"path":"inside.txt"}""");

        Assert.Equal(1, tool.Calls);
        Assert.False(Assert.Single(events.OfType<AgentToolStarted>()).AwaitingApproval);
        Assert.Empty(events.OfType<AgentToolRunning>());
        Assert.Single(events.OfType<AgentToolFinished>());
    }

    // ------------------------------------------------------------------ what the card does with it

    [Fact]
    public Task A_waiting_card_wears_a_still_clock_and_has_no_live_state() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        try
        {
            var call = new ToolCall("call-1", "bash", """{"command":"dotnet build src/Forge.App"}""");

            session.OnAgentEvent(new AgentToolStarted("root", call, AwaitingApproval: true));
            Dispatcher.UIThread.RunJobs();

            var row = Assert.Single(ToolRows(session));
            Assert.True(row.IsAwaitingApproval);
            Assert.False(row.IsRunning, "a call waiting for the user is not running");
            Assert.False(row.IsSucceeded, "nor has it succeeded");
            Assert.False(row.ShowStatusText);
            Assert.Null(row.Live);
            Assert.Equal(SessionStrings.ToolAwaitingApproval.Resolve(), row.CardBody);

            session.OnAgentEvent(new AgentToolRunning("root", call));
            Dispatcher.UIThread.RunJobs();

            Assert.False(row.IsAwaitingApproval);
            Assert.True(row.IsRunning);
            // The live entry (tail, stop, elapsed) is only created for a session that actually runs; this one has no runtime,
            // which is why the waiting phase's null stays null here - what moved is the state, not the tracking rule.
            Assert.Equal(row.Headline, row.CardBody);
            Assert.Same(row, Assert.Single(ToolRows(session)));
        }
        finally
        {
            session.Dispose();
        }
    });

    [Fact]
    public Task The_refusal_closes_the_call_s_own_card_instead_of_adding_a_line() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        try
        {
            var call = new ToolCall("call-1", "bash", """{"command":"rm -rf build"}""");

            session.OnAgentEvent(new AgentToolStarted("root", call, AwaitingApproval: true));
            session.OnAgentEvent(new AgentApprovalDenied("root", call));
            Dispatcher.UIThread.RunJobs();

            var row = Assert.Single(ToolRows(session));
            Assert.False(row.IsAwaitingApproval);
            Assert.False(row.IsRunning);
            Assert.True(row.IsError);
            Assert.False(row.IsSucceeded);
            Assert.Equal(SessionStrings.ToolDenied("bash").Resolve(), row.Result);
            var blocks = Blocks(session);
            Assert.Single(blocks.OfType<TurnToolGroupViewModel>());
            Assert.Empty(blocks.OfType<TurnNoticeViewModel>());
        }
        finally
        {
            session.Dispose();
        }
    });

    [Fact]
    public Task The_waiting_state_and_the_turning_one_are_never_shown_together() => SharedUi.RunAsync(() =>
    {
        var group = new TurnToolGroupViewModel();
        var call = new ToolRowViewModel("bash", """{"command":"dotnet build src/Forge.App"}""");
        call.SetAwaitingApproval(true);
        group.Add(call);

        var window = new Window { Width = 600, Height = 400, Content = new ContentControl { Content = group } };
        window.Show();
        SharedUi.Settle(window);

        FluentIcon Mark(string state) => window.GetVisualDescendants()
            .OfType<FluentIcon>()
            .Single(icon => icon.Classes.Contains("tool-status") && icon.Classes.Contains(state));

        Assert.True(Mark("waiting").IsVisible, "a waiting call shows the still clock");
        Assert.False(Assert.Single(window.GetVisualDescendants().OfType<WaitingGlyph>()).IsVisible);
        Assert.False(Mark("ok").IsVisible);
        Assert.False(Mark("fail").IsVisible);

        call.SetAwaitingApproval(false);
        SharedUi.Settle(window);

        Assert.False(Mark("waiting").IsVisible);
        Assert.True(Assert.Single(window.GetVisualDescendants().OfType<WaitingGlyph>()).IsVisible, "once it runs, the clock turns");
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public void Ending_a_turn_closes_a_call_that_was_still_waiting()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        var card = transcript.BeginTurn(DateTimeOffset.Now);
        var row = card.AddTool(new ToolCall("call-1", "bash", """{"command":"rm -rf build"}"""));
        row.SetAwaitingApproval(true);

        // A turn can end while its call still waits (the turn was stopped): the card must not keep a waiting mark forever.
        card.Finish(elapsed: null, error: null);

        Assert.False(row.IsAwaitingApproval);
        Assert.False(row.IsRunning);
        Assert.True(row.IsError);
        Assert.Equal(SessionStrings.NoToolResult.Resolve(), row.Result);
        Assert.False(Assert.Single(card.Blocks.OfType<TurnToolGroupViewModel>()).IsRunning);
    }

    [Fact]
    public void A_call_that_arrives_after_its_turn_ended_is_closed_at_once()
    {
        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
        var card = transcript.BeginTurn(DateTimeOffset.Now);
        card.Finish(elapsed: null, error: null);

        // The coalescer runs at Background, so a start event can land after its own turn's end; nothing would ever close it.
        var row = card.AddTool(new ToolCall("call-1", "bash", """{"command":"ls"}"""));
        row.SetAwaitingApproval(true);

        Assert.False(row.IsRunning);
        Assert.False(row.IsAwaitingApproval);
        Assert.True(row.IsError);
        Assert.Equal(SessionStrings.NoToolResult.Resolve(), row.Result);
    }

    private async Task<SessionWorkspaceViewModel> SessionAsync()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        return new SessionWorkspaceViewModel(
            root,
            runtime: null,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
    }

    /// <summary>Every block the transcript's cards hold right now, in the order the rounds keep them.</summary>
    private static List<TurnBlockViewModel> Blocks(SessionWorkspaceViewModel session) =>
        session.Transcript.Rounds
            .SelectMany(round => round.Card is { } card ? (IEnumerable<TurnBlockViewModel>)card.Blocks : Array.Empty<TurnBlockViewModel>())
            .ToList();

    /// <summary>Every tool row the transcript holds right now, in the order the rounds and their cards keep them.</summary>
    private static List<ToolRowViewModel> ToolRows(SessionWorkspaceViewModel session)
    {
        var rows = new List<ToolRowViewModel>();
        foreach (var round in session.Transcript.Rounds)
        {
            if (round.Card is not { } card)
            {
                continue;
            }

            foreach (var block in card.Blocks)
            {
                if (block is TurnToolGroupViewModel group)
                {
                    rows.AddRange(group.Tools);
                }
                else if (block is ToolRowViewModel single)
                {
                    rows.Add(single);
                }
            }
        }

        return rows;
    }
}
