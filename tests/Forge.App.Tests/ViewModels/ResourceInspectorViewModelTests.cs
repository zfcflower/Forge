using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ResourceInspectorViewModel"/> (wave W8): the panel pulls the active session's live tool calls and
/// sub-agents, reconciles rows instead of rebuilding them, orders them by start time (a queued child starts at
/// <see cref="DateTimeOffset.MinValue"/> and sorts first), and drives its timer from the live preferences. The timer is
/// injected, so nothing here needs a message loop: <see cref="ResourceInspectorViewModel.Refresh"/> is called directly and
/// a fake ticker fires the panel's own callback.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ResourceInspectorViewModelTests
{
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the empty states

    [Fact]
    public async Task No_session_means_no_rows_and_the_no_session_text()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var source = new SessionSource { Present = false };
                using var model = Create(source, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                return (model.HasSession, model.HasRunning, model.IsEmpty, model.Items.Count, model.NoSession);
            },
            CancellationToken.None);

        Assert.False(state.HasSession);
        Assert.False(state.HasRunning);
        Assert.False(state.IsEmpty);
        Assert.Equal(0, state.Count);
        Assert.Equal("没有打开的会话", state.NoSession);
    }

    [Fact]
    public async Task An_idle_session_is_empty_but_present()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var source = new SessionSource();
                using var model = Create(source, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                return (model.HasSession, model.HasRunning, model.IsEmpty, model.CountText, model.Title);
            },
            CancellationToken.None);

        Assert.True(state.HasSession);
        Assert.False(state.HasRunning);
        Assert.True(state.IsEmpty);
        Assert.Equal("0 项进行中", state.CountText);
        Assert.Equal("运行中的资源", state.Title);
    }

    // ---------------------------------------------------------------- rows

    [Fact]
    public async Task Rows_are_the_live_tools_and_sub_agents_in_start_order()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var source = new SessionSource();
                var queued = Agent("child-queued", startedAt: null);
                var running = Agent("child-running", At.AddSeconds(10));
                var tool = Tool(At.AddSeconds(5));
                // Deliberately out of order: the panel sorts, it does not echo the session's own list order.
                source.SubAgents.Add(running);
                source.SubAgents.Add(queued);
                source.Tools.Add(tool);

                using var model = Create(source, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                return (Rows: model.Items.ToArray(), model.CountText, model.HasRunning, model.IsEmpty, Queued: queued, Tool: tool, Running: running);
            },
            CancellationToken.None);

        Assert.Equal(3, state.Rows.Length);
        // The queued child's StartedAt is DateTimeOffset.MinValue, so it precedes everything that actually started.
        Assert.Same(state.Queued, state.Rows[0]);
        Assert.Same(state.Tool, state.Rows[1]);
        Assert.Same(state.Running, state.Rows[2]);
        Assert.Equal("3 项进行中", state.CountText);
        Assert.True(state.HasRunning);
        Assert.False(state.IsEmpty);
    }

    [Fact]
    public async Task A_refresh_keeps_unchanged_rows_and_removes_the_ones_that_stopped()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var source = new SessionSource();
                source.SubAgents.Add(Agent("child-running", At.AddSeconds(10)));
                source.Tools.Add(Tool(At.AddSeconds(5)));
                using var model = Create(source, new AppPreferences(), new FakeScheduler());

                model.Refresh();
                var first = model.Items.ToArray();
                model.Refresh();
                var second = model.Items.ToArray();

                source.Tools.Clear();
                model.Refresh();
                var third = model.Items.ToArray();
                return (First: first, Second: second, Third: third, model.HasRunning);
            },
            CancellationToken.None);

        // A second pull over the same live work changes nothing, and the rows keep their identity (no rebuild).
        Assert.Equal(2, state.First.Length);
        Assert.Same(state.First[0], state.Second[0]);
        Assert.Same(state.First[1], state.Second[1]);

        // The tool finished: only its row goes, the sub-agent's row is still the same instance.
        Assert.Single(state.Third);
        Assert.Same(state.First[1], state.Third[0]);
        Assert.True(state.HasRunning);
    }

    [Fact]
    public async Task A_session_that_disappears_leaves_no_rows()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var source = new SessionSource();
                source.Tools.Add(Tool(At));
                using var model = Create(source, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                var before = model.Items.Count;

                source.Present = false;
                model.Refresh();
                return (Before: before, model.Items.Count, model.HasSession, model.HasRunning, model.IsEmpty);
            },
            CancellationToken.None);

        Assert.Equal(1, state.Before);
        Assert.Equal(0, state.Count);
        Assert.False(state.HasSession);
        Assert.False(state.HasRunning);
        Assert.False(state.IsEmpty);
    }

    // ---------------------------------------------------------------- the timer and the preferences

    [Fact]
    public async Task Start_schedules_one_pull_at_the_preference_interval_and_pulls_at_once()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var source = new SessionSource();
                source.Tools.Add(Tool(At));
                var scheduler = new FakeScheduler();
                using var model = Create(source, new AppPreferences(), scheduler);

                model.Start();
                return (Created: scheduler.Created, Interval: scheduler.Last.Interval, Rows: model.Items.Count);
            },
            CancellationToken.None);

        Assert.Equal(1, state.Created);
        Assert.Equal(TimeSpan.FromMilliseconds(AppPreferences.DefaultInspectorRefreshMilliseconds), state.Interval);
        Assert.Equal(1, state.Rows);
    }

    [Fact]
    public async Task A_new_interval_rebuilds_the_timer_and_disposes_the_old_handle()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var scheduler = new FakeScheduler();
                using var model = Create(new SessionSource(), preferences, scheduler);
                model.Start();
                var first = scheduler.Last;

                preferences.InspectorRefreshMilliseconds = 250;
                return (Created: scheduler.Created, OldDisposals: first.Disposals, NewInterval: scheduler.Last.Interval);
            },
            CancellationToken.None);

        Assert.Equal(2, state.Created);
        Assert.Equal(1, state.OldDisposals);
        Assert.Equal(TimeSpan.FromMilliseconds(250), state.NewInterval);
    }

    [Fact]
    public async Task Ticking_the_timer_pulls_the_session_again()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var source = new SessionSource();
                var scheduler = new FakeScheduler();
                using var model = Create(source, new AppPreferences(), scheduler);
                model.Start();
                var before = model.Items.Count;

                source.Tools.Add(Tool(At));
                scheduler.Last.Fire();
                return (Before: before, After: model.Items.Count);
            },
            CancellationToken.None);

        Assert.Equal(0, state.Before);
        Assert.Equal(1, state.After);
    }

    [Fact]
    public async Task A_disposed_panel_neither_reschedules_nor_pulls()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var scheduler = new FakeScheduler();
                var source = new SessionSource();
                var model = Create(source, preferences, scheduler);
                model.Start();
                var ticker = scheduler.Last;
                model.Dispose();

                preferences.InspectorRefreshMilliseconds = 5000;
                var rescheduled = scheduler.Created;
                source.Tools.Add(Tool(At));
                ticker.Fire();
                return (rescheduled, ticker.Disposals, model.Items.Count, CanClose: model.CloseCommand.CanExecute(null));
            },
            CancellationToken.None);

        Assert.Equal(1, state.rescheduled);
        Assert.Equal(1, state.Disposals);
        Assert.Equal(0, state.Count);
        Assert.False(state.CanClose);
    }

    [Fact]
    public async Task A_new_line_limit_pulls_immediately()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var preferences = new AppPreferences();
                var source = new SessionSource();
                var tool = Tool(At);
                var snapshot = string.Join("\n", Enumerable.Range(1, 25).Select(static line => "line " + line));
                tool.RecordTail(snapshot);
                source.Tools.Add(tool);
                using var model = Create(source, preferences, new FakeScheduler());
                model.Refresh();
                var wide = tool.TailText;

                // The setter clamps, so 20 is the shortest tail a user can ask for; the change must land without a timer tick.
                preferences.InspectorLogTailLines = AppPreferences.MinInspectorLogTailLines;
                return (Wide: wide, Narrow: tool.TailText, Snapshot: snapshot);
            },
            CancellationToken.None);

        Assert.Equal(state.Snapshot, state.Wide);
        Assert.Equal(string.Join("\n", Enumerable.Range(6, 20).Select(static line => "line " + line)), state.Narrow);
    }

    // ---------------------------------------------------------------- close and language

    [Fact]
    public async Task Close_runs_the_callback_once_and_is_disabled_after_dispose()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var closed = 0;
                var model = Create(new SessionSource(), new AppPreferences(), new FakeScheduler(), close: () => closed++);
                var canClose = model.CloseCommand.CanExecute(null);
                model.CloseCommand.Execute(null);
                var afterClick = closed;

                model.Dispose();
                var canCloseAfter = model.CloseCommand.CanExecute(null);
                model.CloseCommand.Execute(null);
                return (CanClose: canClose, AfterClick: afterClick, CanCloseAfter: canCloseAfter, Closed: closed);
            },
            CancellationToken.None);

        Assert.True(state.CanClose);
        Assert.Equal(1, state.AfterClick);
        Assert.False(state.CanCloseAfter);
        Assert.Equal(1, state.Closed);
    }

    [Fact]
    public async Task A_language_switch_re_raises_the_chrome_and_the_rows()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var source = new SessionSource();
                var tool = Tool(At, agentId: "root");
                source.Tools.Add(tool);
                source.SubAgents.Add(Agent("child-running", At.AddSeconds(1)));
                using var model = Create(source, new AppPreferences(), new FakeScheduler());
                model.Refresh();

                var raised = new List<string?>();
                var toolRaised = new List<string?>();
                var agentRaised = new List<string?>();
                model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                tool.PropertyChanged += (_, e) => toolRaised.Add(e.PropertyName);
                source.SubAgents[0].PropertyChanged += (_, e) => agentRaised.Add(e.PropertyName);

                var chinese = (model.Title, model.Empty, model.NoSession, model.CountText, Label: tool.AgentLabel);
                Loc.Instance.Language = UiLanguage.English;
                return (
                    Chinese: chinese,
                    English: (model.Title, model.Empty, model.NoSession, model.CountText, Label: tool.AgentLabel),
                    Raised: raised,
                    ToolRaised: toolRaised,
                    AgentRaised: agentRaised);
            },
            CancellationToken.None);

        Assert.Equal("运行中的资源", state.Chinese.Title);
        Assert.Equal("Running resources", state.English.Title);
        Assert.Equal("Nothing is running in this session", state.English.Empty);
        Assert.Equal("No session is open", state.English.NoSession);
        Assert.Equal("2 active", state.English.CountText);
        Assert.Equal("Main agent", state.English.Label);

        foreach (var name in new[]
                 {
                     nameof(ResourceInspectorViewModel.Title), nameof(ResourceInspectorViewModel.Empty),
                     nameof(ResourceInspectorViewModel.NoSession), nameof(ResourceInspectorViewModel.CountText),
                 })
        {
            Assert.Contains(name, state.Raised);
        }

        // The rows re-derive their own texts; the panel does not rebuild them.
        Assert.Contains(nameof(LiveToolViewModel.AgentLabel), state.ToolRaised);
        Assert.Contains(nameof(LiveToolViewModel.ElapsedText), state.ToolRaised);
        Assert.Contains(nameof(SubAgentViewModel.Kind), state.AgentRaised);
    }

    // ---------------------------------------------------------------- helpers

    private static ResourceInspectorViewModel Create(
        SessionSource source,
        AppPreferences preferences,
        FakeScheduler scheduler,
        Action? close = null) =>
        new(source.Read, preferences, close, scheduler.Schedule);

    private static LiveToolViewModel Tool(DateTimeOffset startedAt, string agentId = "root") =>
        new(agentId, static () => "Scout", new ToolCall("call-1", "Bash", """{"command":"npm test"}"""), startedAt, static () => { });

    private static SubAgentViewModel Agent(string agentId, DateTimeOffset? startedAt)
    {
        var agent = new SubAgentViewModel("session", agentId, agentId + "-session", "Explore", "look at the diff", "model", stopRequested: null);
        if (startedAt is { } at)
        {
            agent.MarkRunning(at);
        }

        return agent;
    }

    /// <summary>The active session as the panel reads it; one instance per pull, over the test's own mutable lists.</summary>
    private sealed class SessionSource
    {
        public bool Present { get; set; } = true;

        public string Title { get; set; } = "demo session";

        public List<LiveToolViewModel> Tools { get; } = [];

        public List<SubAgentViewModel> SubAgents { get; } = [];

        public ResourceInspectorSession? Read() =>
            Present ? new ResourceInspectorSession(Title, Tools, SubAgents) : null;
    }

    /// <summary>Records every timer the panel asks for; a test fires the callback or asserts the handle was disposed.</summary>
    private sealed class FakeScheduler
    {
        private readonly List<FakeTicker> tickers = [];

        public Func<TimeSpan, Action, IDisposable> Schedule => (interval, tick) =>
        {
            var ticker = new FakeTicker(interval, tick);
            tickers.Add(ticker);
            return ticker;
        };

        public int Created => tickers.Count;

        public FakeTicker Last => tickers[^1];
    }

    private sealed class FakeTicker(TimeSpan interval, Action tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;

        public int Disposals { get; private set; }

        public void Fire() => tick();

        public void Dispose() => Disposals++;
    }
}