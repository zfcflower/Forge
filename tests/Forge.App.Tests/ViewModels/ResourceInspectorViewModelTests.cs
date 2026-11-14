using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ResourceInspectorViewModel"/> (wave W8): the panel pulls the live tool calls and sub-agents of every open
/// session, lists them grouped by session (a header per session that runs something, the active session first), reconciles
/// rows instead of rebuilding them, orders them by start time inside a group (a queued child starts at
/// <see cref="DateTimeOffset.MinValue"/> and sorts first), listens to every open session for a coalesced refresh and lets go
/// of a session when it closes and of all of them on dispose, and drives its timer from the live preferences. The timer and
/// the post are injected, so nothing here needs a message loop.
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
                var window = new Window();
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
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
    public async Task An_idle_session_is_empty_but_present_and_has_no_header()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var window = new Window();
                window.Open("demo session");
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                return (model.HasSession, model.HasRunning, model.IsEmpty, model.CountText, model.Title, model.Items.Count, model.Empty);
            },
            CancellationToken.None);

        Assert.True(state.HasSession);
        Assert.False(state.HasRunning);
        Assert.True(state.IsEmpty);
        Assert.Equal("0 项进行中", state.CountText);
        Assert.Equal("运行中的资源", state.Title);
        Assert.Equal(0, state.Count);
        Assert.Equal("打开的会话都没有正在运行的工具或子 Agent", state.Empty);
    }

    // ---------------------------------------------------------------- rows of one session

    [Fact]
    public async Task Rows_are_the_live_tools_and_sub_agents_in_start_order_under_their_header()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var window = new Window();
                var source = window.Open("demo session");
                var queued = Agent("child-queued", startedAt: null);
                var running = Agent("child-running", At.AddSeconds(10));
                var tool = Tool(At.AddSeconds(5));
                // Deliberately out of order: the panel sorts, it does not echo the session's own list order.
                source.Rows.Add(running);
                source.Rows.Add(tool);
                source.Rows.Add(queued);

                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                return (Rows: model.Items.ToArray(), model.CountText, model.RunningCount, model.HasRunning, model.IsEmpty, Queued: queued, Tool: tool, Running: running);
            },
            CancellationToken.None);

        Assert.Equal(4, state.Rows.Length);
        var header = Assert.IsType<ResourceInspectorGroupViewModel>(state.Rows[0]);
        Assert.Equal("demo session", header.Title);
        Assert.Equal(3, header.Count);
        Assert.Equal("3 项进行中", header.CountText);
        Assert.True(header.IsActive);
        Assert.Same(state.Queued, state.Rows[1]);
        Assert.Same(state.Tool, state.Rows[2]);
        Assert.Same(state.Running, state.Rows[3]);
        // The header is not a resource: the totals count the three rows only.
        Assert.Equal(3, state.RunningCount);
        Assert.Equal("3 项进行中", state.CountText);
        Assert.True(state.HasRunning);
        Assert.False(state.IsEmpty);
    }

    [Fact]
    public async Task Equal_start_times_keep_one_order_across_pulls()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var source = window.Open("demo session");
                var b = Tool(At, callId: "call-b");
                var a = Tool(At, callId: "call-a");
                source.Rows.Add(b);
                source.Rows.Add(a);
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                var first = model.Items.ToArray();
                source.Rows.Reverse();
                model.Refresh();
                return (First: first, Second: model.Items.ToArray(), A: a, B: b);
            },
            CancellationToken.None);

        Assert.Same(state.A, state.First[1]);
        Assert.Same(state.B, state.First[2]);
        Assert.Equal(state.First, state.Second);
    }

    [Fact]
    public async Task A_refresh_keeps_unchanged_rows_and_removes_the_ones_that_stopped()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var source = window.Open("demo session");
                var agent = Agent("child-running", At.AddSeconds(10));
                var tool = Tool(At.AddSeconds(5));
                source.Rows.Add(agent);
                source.Rows.Add(tool);
                using var model = Create(window, new AppPreferences(), new FakeScheduler());

                model.Refresh();
                var first = model.Items.ToArray();
                var changes = 0;
                ((System.Collections.Specialized.INotifyCollectionChanged)model.Items).CollectionChanged += (_, _) => changes++;
                model.Refresh();
                var second = model.Items.ToArray();
                var changesOnSteadyPull = changes;

                source.Rows.Remove(tool);
                model.Refresh();
                var third = model.Items.ToArray();
                return (First: first, Second: second, Third: third, Steady: changesOnSteadyPull, model.HasRunning, Agent: agent);
            },
            CancellationToken.None);

        // A second pull over the same live work changes nothing, and the rows keep their identity (no rebuild).
        Assert.Equal(3, state.First.Length);
        Assert.Equal(state.First, state.Second);
        Assert.Equal(0, state.Steady);

        // The tool finished: only its row goes; the header and the sub-agent's row are the same instances.
        Assert.Equal(2, state.Third.Length);
        Assert.Same(state.First[0], state.Third[0]);
        Assert.Same(state.Agent, state.Third[1]);
        Assert.True(state.HasRunning);
    }

    // ---------------------------------------------------------------- several sessions

    [Fact]
    public async Task Two_sessions_are_listed_in_their_own_groups_with_the_active_one_first()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.English);
                var window = new Window();
                var first = window.Open("first session");
                var second = window.Open("second session");
                var firstTool = Tool(At, callId: "call-1");
                var secondTool = Tool(At.AddSeconds(-30), callId: "call-1");
                var secondAgent = Agent("child", At.AddSeconds(1));
                first.Rows.Add(firstTool);
                second.Rows.Add(secondTool);
                second.Rows.Add(secondAgent);
                window.Active = second;

                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                var rows = model.Items.ToArray();
                var headers = rows.OfType<ResourceInspectorGroupViewModel>().ToArray();
                return (Rows: rows, Headers: headers, model.RunningCount, model.CountText, FirstTool: firstTool, SecondTool: secondTool, SecondAgent: secondAgent, Active: headers[0].ActiveText);
            },
            CancellationToken.None);

        Assert.Equal(5, state.Rows.Length);
        Assert.Equal(2, state.Headers.Length);
        // The active session leads even though the window lists it second; each group's rows follow its own header.
        Assert.Same(state.Headers[0], state.Rows[0]);
        Assert.Equal("second session", state.Headers[0].Title);
        Assert.True(state.Headers[0].IsActive);
        Assert.Equal(2, state.Headers[0].Count);
        Assert.Same(state.SecondTool, state.Rows[1]);
        Assert.Same(state.SecondAgent, state.Rows[2]);
        Assert.Same(state.Headers[1], state.Rows[3]);
        Assert.Equal("first session", state.Headers[1].Title);
        Assert.False(state.Headers[1].IsActive);
        Assert.Same(state.FirstTool, state.Rows[4]);
        Assert.Equal(3, state.RunningCount);
        Assert.Equal("3 active", state.CountText);
        Assert.Equal("Current session", state.Active);
    }

    [Fact]
    public async Task Switching_the_active_session_moves_its_group_first_without_rebuilding_rows()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var first = window.Open("first session");
                var second = window.Open("second session");
                var a = Tool(At, callId: "a");
                var b = Tool(At, callId: "b");
                first.Rows.Add(a);
                second.Rows.Add(b);
                window.Active = first;
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                var before = model.Items.ToArray();

                window.Active = second;
                model.Refresh();
                return (Before: before, After: model.Items.ToArray());
            },
            CancellationToken.None);

        Assert.Equal(4, state.After.Length);
        Assert.Same(state.Before[2], state.After[0]);
        Assert.Same(state.Before[3], state.After[1]);
        Assert.Same(state.Before[0], state.After[2]);
        Assert.Same(state.Before[1], state.After[3]);
        Assert.True(((ResourceInspectorGroupViewModel)state.After[0]).IsActive);
        Assert.False(((ResourceInspectorGroupViewModel)state.After[2]).IsActive);
    }

    [Fact]
    public async Task A_session_that_closes_drops_its_rows_and_its_listener()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var keep = window.Open("kept");
                var closing = window.Open("closing");
                keep.Rows.Add(Tool(At, callId: "k"));
                closing.Rows.Add(Tool(At, callId: "c"));
                closing.Rows.Add(Agent("child", At));
                var posts = new FakePost();
                using var model = Create(window, new AppPreferences(), new FakeScheduler(), post: posts);
                model.Refresh();
                var before = (model.Items.Count, model.ListenedSessions, closing.Listeners);

                // The shell takes the session out of its list, then disposes it; its close raises the changed event once.
                window.Close(closing);
                var queued = posts.Pending;
                posts.RunAll();
                return (before, Queued: queued, model.Items.Count, model.ListenedSessions, ClosingListeners: closing.Listeners, KeptListeners: keep.Listeners, model.RunningCount, Rows: model.Items.ToArray());
            },
            CancellationToken.None);

        Assert.Equal(5, state.before.Count);
        Assert.Equal(2, state.before.ListenedSessions);
        Assert.Equal(1, state.before.Listeners);
        Assert.Equal(1, state.Queued);
        Assert.Equal(2, state.Count);
        Assert.Equal(1, state.ListenedSessions);
        Assert.Equal(0, state.ClosingListeners);
        Assert.Equal(1, state.KeptListeners);
        Assert.Equal(1, state.RunningCount);
        Assert.Equal("kept", Assert.IsType<ResourceInspectorGroupViewModel>(state.Rows[0]).Title);
    }

    [Fact]
    public async Task A_session_opened_later_is_listed_and_listened_to_from_the_next_pull()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                window.Open("first");
                var posts = new FakePost();
                using var model = Create(window, new AppPreferences(), new FakeScheduler(), post: posts);
                model.Refresh();

                var later = window.Open("later");
                model.Refresh();
                var listened = later.Listeners;

                // Its first call now reaches the panel through the coalesced refresh, without waiting for the timer.
                later.Rows.Add(Tool(At));
                later.RaiseChanged();
                posts.RunAll();
                return (Listened: listened, model.Items.Count, model.RunningCount);
            },
            CancellationToken.None);

        Assert.Equal(1, state.Listened);
        Assert.Equal(2, state.Count);
        Assert.Equal(1, state.RunningCount);
    }

    [Fact]
    public async Task A_burst_of_change_events_is_coalesced_into_one_refresh()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var first = window.Open("first");
                var second = window.Open("second");
                var posts = new FakePost();
                using var model = Create(window, new AppPreferences(), new FakeScheduler(), post: posts);
                model.Refresh();

                for (var index = 0; index < 10; index++)
                {
                    first.RaiseChanged();
                    second.RaiseChanged();
                }

                var queued = posts.Pending;
                posts.RunAll();

                // Once the queued refresh ran, the next change queues a new one.
                first.RaiseChanged();
                return (Queued: queued, Requeued: posts.Pending);
            },
            CancellationToken.None);

        Assert.Equal(1, state.Queued);
        Assert.Equal(1, state.Requeued);
    }

    [Fact]
    public async Task Dispose_unsubscribes_from_every_session_and_ignores_late_change_events()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var first = window.Open("first");
                var second = window.Open("second");
                first.Rows.Add(Tool(At));
                var posts = new FakePost();
                var model = Create(window, new AppPreferences(), new FakeScheduler(), post: posts);
                model.Start();
                var before = (first.Listeners, second.Listeners);

                model.Dispose();
                first.RaiseChanged();
                return (before, After: (first.Listeners, second.Listeners), model.ListenedSessions, posts.Pending, model.Items.Count);
            },
            CancellationToken.None);

        Assert.Equal((1, 1), state.before);
        Assert.Equal((0, 0), state.After);
        Assert.Equal(0, state.ListenedSessions);
        Assert.Equal(0, state.Pending);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public async Task A_row_stops_its_own_call_in_its_own_session()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var first = window.Open("first");
                var second = window.Open("second");
                var firstStops = new List<(string, string)>();
                var secondStops = new List<(string, string)>();
                // The same call id in two sessions: only the session that owns the row may receive the stop.
                var firstTool = Tool(At, callId: "call-1", stop: (agent, call) => { firstStops.Add((agent, call)); return true; });
                var secondTool = Tool(At, callId: "call-1", stop: (agent, call) => { secondStops.Add((agent, call)); return true; });
                first.Rows.Add(firstTool);
                second.Rows.Add(secondTool);
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();

                var row = model.Items.OfType<LiveToolViewModel>().Single(candidate => ReferenceEquals(candidate, secondTool));
                row.StopCommand.Execute(null);
                return (First: firstStops.ToArray(), Second: secondStops.ToArray(), FirstCanStop: firstTool.CanStop, SecondCanStop: secondTool.CanStop, secondTool.IsStopRequested);
            },
            CancellationToken.None);

        Assert.Empty(state.First);
        Assert.Equal([("root", "call-1")], state.Second);
        Assert.True(state.FirstCanStop);
        Assert.False(state.SecondCanStop);
        Assert.True(state.IsStopRequested);
    }

    // ---------------------------------------------------------------- the timer and the preferences

    [Fact]
    public async Task Start_schedules_one_pull_at_the_preference_interval_and_pulls_at_once()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                window.Open("demo").Rows.Add(Tool(At));
                var scheduler = new FakeScheduler();
                using var model = Create(window, new AppPreferences(), scheduler);

                model.Start();
                return (Created: scheduler.Created, Interval: scheduler.Last.Interval, Rows: model.RunningCount);
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
                var window = new Window();
                window.Open("demo");
                using var model = Create(window, preferences, scheduler);
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
    public async Task Ticking_the_timer_pulls_the_sessions_again()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var window = new Window();
                var source = window.Open("demo");
                var scheduler = new FakeScheduler();
                using var model = Create(window, new AppPreferences(), scheduler);
                model.Start();
                var before = model.RunningCount;

                source.Rows.Add(Tool(At));
                scheduler.Last.Fire();
                return (Before: before, After: model.RunningCount);
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
                var window = new Window();
                var source = window.Open("demo");
                var model = Create(window, preferences, scheduler);
                model.Start();
                var ticker = scheduler.Last;
                model.Dispose();

                preferences.InspectorRefreshMilliseconds = 5000;
                var rescheduled = scheduler.Created;
                source.Rows.Add(Tool(At));
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
                var window = new Window();
                var tool = Tool(At);
                var snapshot = string.Join("\n", Enumerable.Range(1, 25).Select(static line => "line " + line));
                tool.RecordTail(snapshot);
                window.Open("demo").Rows.Add(tool);
                using var model = Create(window, preferences, new FakeScheduler());
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
                var model = Create(new Window(), new AppPreferences(), new FakeScheduler(), close: () => closed++);
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
    public async Task A_language_switch_re_raises_the_chrome_the_headers_and_the_rows()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var window = new Window();
                var source = window.Open("demo");
                var tool = Tool(At, agentId: "root");
                var agent = Agent("child-running", At.AddSeconds(1));
                source.Rows.Add(tool);
                source.Rows.Add(agent);
                using var model = Create(window, new AppPreferences(), new FakeScheduler());
                model.Refresh();
                var header = model.Items.OfType<ResourceInspectorGroupViewModel>().Single();

                var raised = new List<string?>();
                var toolRaised = new List<string?>();
                var agentRaised = new List<string?>();
                var headerRaised = new List<string?>();
                model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                tool.PropertyChanged += (_, e) => toolRaised.Add(e.PropertyName);
                agent.PropertyChanged += (_, e) => agentRaised.Add(e.PropertyName);
                header.PropertyChanged += (_, e) => headerRaised.Add(e.PropertyName);

                var chinese = (model.Title, model.Empty, model.NoSession, model.CountText, Label: tool.AgentLabel, Active: header.ActiveText);
                Loc.Instance.Language = UiLanguage.English;
                return (
                    Chinese: chinese,
                    English: (model.Title, model.Empty, model.NoSession, model.CountText, Label: tool.AgentLabel, Active: header.ActiveText, Group: header.CountText),
                    Raised: raised,
                    ToolRaised: toolRaised,
                    AgentRaised: agentRaised,
                    HeaderRaised: headerRaised);
            },
            CancellationToken.None);

        Assert.Equal("运行中的资源", state.Chinese.Title);
        Assert.Equal("当前会话", state.Chinese.Active);
        Assert.Equal("Running resources", state.English.Title);
        Assert.Equal("Nothing is running in the open sessions", state.English.Empty);
        Assert.Equal("No session is open", state.English.NoSession);
        Assert.Equal("2 active", state.English.CountText);
        Assert.Equal("2 active", state.English.Group);
        Assert.Equal("Main agent", state.English.Label);
        Assert.Equal("Current session", state.English.Active);

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
        Assert.Contains(nameof(ResourceInspectorGroupViewModel.CountText), state.HeaderRaised);
        Assert.Contains(nameof(ResourceInspectorGroupViewModel.ActiveText), state.HeaderRaised);
    }

    // ---------------------------------------------------------------- helpers

    private static ResourceInspectorViewModel Create(
        Window window,
        AppPreferences preferences,
        FakeScheduler scheduler,
        Action? close = null,
        FakePost? post = null) =>
        new(window.Collect, () => window.Active, preferences, close, scheduler.Schedule, post is null ? null : post.Post);

    private static LiveToolViewModel Tool(
        DateTimeOffset startedAt,
        string agentId = "root",
        string callId = "call-1",
        Func<string, string, bool>? stop = null) =>
        new(agentId, static () => "Scout", new ToolCall(callId, "Bash", """{"command":"npm test"}"""), startedAt, stop ?? (static (_, _) => true));

    private static SubAgentViewModel Agent(string agentId, DateTimeOffset? startedAt)
    {
        var agent = new SubAgentViewModel("session", agentId, agentId + "-session", "Explore", "look at the diff", "model", stopRequested: null);
        if (startedAt is { } at)
        {
            agent.MarkRunning(at);
        }

        return agent;
    }

    /// <summary>The shell as the panel reads it: open sessions in order and the active one.</summary>
    private sealed class Window
    {
        private readonly List<FakeSource> open = [];

        public IResourceInspectorSource? Active { get; set; }

        public FakeSource Open(string title)
        {
            var source = new FakeSource(title);
            open.Add(source);
            Active ??= source;
            return source;
        }

        /// <summary>Like the shell: out of the list first, then disposed (which raises the changed event once).</summary>
        public void Close(FakeSource source)
        {
            open.Remove(source);
            if (ReferenceEquals(Active, source))
            {
                Active = open.Count > 0 ? open[0] : null;
            }

            source.Dispose();
        }

        public void Collect(List<IResourceInspectorSource> into) => into.AddRange(open);
    }

    private sealed class FakeSource(string title) : IResourceInspectorSource
    {
        private EventHandler? changed;
        private bool disposed;

        public string Title { get; } = title;

        public List<object> Rows { get; } = [];

        public int Listeners => changed?.GetInvocationList().Length ?? 0;

        public event EventHandler? RunningResourcesChanged
        {
            add => changed += value;
            remove => changed -= value;
        }

        public void CollectRunning(List<object> into)
        {
            if (!disposed)
            {
                into.AddRange(Rows);
            }
        }

        public void RaiseChanged() => changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            disposed = true;
            RaiseChanged();
        }
    }

    /// <summary>Queues posted refreshes until the test runs them, so coalescing is observable.</summary>
    private sealed class FakePost
    {
        private readonly List<Action> queue = [];

        public int Pending => queue.Count;

        public void Post(Action action) => queue.Add(action);

        public void RunAll()
        {
            var run = queue.ToArray();
            queue.Clear();
            foreach (var action in run)
            {
                action();
            }
        }
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
