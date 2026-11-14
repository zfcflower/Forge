using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins <see cref="LiveToolViewModel"/> (wave W8): the snapshot a tool's output thread publishes stays invisible until a
/// UI thread tick reads it, every notification is raised from that tick (and only when the snapshot or the line limit
/// changed), and a finished call is inert. No UI thread is needed: the type is a view model over one volatile snapshot.
/// </summary>
public sealed class LiveToolViewModelTests
{
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_snapshot_published_on_the_producers_thread_is_visible_only_after_a_tick()
    {
        var tool = Create();

        await Task.Run(() => tool.RecordTail("npm test\nrunning 3 suites"));
        Assert.False(tool.HasTail);
        Assert.Empty(tool.TailText);

        tool.Tick(AppPreferences.DefaultInspectorLogTailLines, At.AddSeconds(1));

        Assert.True(tool.HasTail);
        Assert.Equal("npm test\nrunning 3 suites", tool.TailText);
    }

    [Fact]
    public void A_tick_raises_the_tail_only_when_the_snapshot_or_the_line_limit_changed()
    {
        var tool = Create();
        var raised = new List<string?>();
        tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Two ticks over the same snapshot and the same limit: only the first re-cuts and notifies.
        tool.RecordTail("one\ntwo");
        tool.Tick(10, At);
        tool.Tick(10, At);
        Assert.Equal(1, TailNotifications(raised));

        // A new snapshot re-cuts.
        tool.RecordTail("one\ntwo\nthree");
        tool.Tick(10, At);
        Assert.Equal(2, TailNotifications(raised));

        // So does a new line limit over the same snapshot.
        tool.Tick(2, At);
        Assert.Equal(3, TailNotifications(raised));
        Assert.Equal("two\nthree", tool.TailText);
    }

    [Theory]
    [InlineData("a\nb\nc\nd", "c\nd")]
    [InlineData("a\nb\nc\n", "b\nc\n")]
    public void The_tail_keeps_the_last_lines_with_a_trailing_newline_never_counting_as_one(string snapshot, string expected)
    {
        var tool = Create();

        tool.RecordTail(snapshot);
        tool.Tick(2, At);

        Assert.Equal(expected, tool.TailText);
    }

    [Fact]
    public void The_tick_restamps_the_elapsed_reading_in_the_current_language()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var tool = Create();
        Assert.Equal("0.0 秒", tool.ElapsedText);

        tool.Tick(AppPreferences.DefaultInspectorLogTailLines, At.AddMilliseconds(4200));
        Assert.Equal("4.2 秒", tool.ElapsedText);

        Loc.Instance.Language = UiLanguage.English;
        Assert.Equal("4.2s", tool.ElapsedText);
    }

    [Fact]
    public void A_finished_call_releases_its_snapshot_and_is_inert()
    {
        var tool = Create();
        tool.RecordTail("output");
        tool.Tick(200, At.AddSeconds(1));
        Assert.True(tool.HasTail);

        tool.Finish();
        Assert.False(tool.IsRunning);

        // The row's transcript outlives the call and its result already carries the whole output, so the entry does not
        // keep the last snapshot alive.
        Assert.False(tool.HasTail);
        Assert.Empty(tool.TailText);

        var raised = new List<string?>();
        tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tool.Tick(200, At.AddSeconds(10));
        tool.RecordTail("more output");
        tool.Tick(200, At.AddSeconds(11));

        Assert.Empty(raised);
        Assert.False(tool.HasTail);
        Assert.Empty(tool.TailText);
    }

    [Fact]
    public void The_stop_command_asks_for_exactly_this_call_once_and_then_reads_stopping()
    {
        var stops = new List<(string Agent, string Call)>();
        var tool = Create("child-1", stop: (agent, call) =>
        {
            stops.Add((agent, call));
            return true;
        });
        var raised = new List<string?>();
        tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.True(tool.CanStop);
        Assert.True(tool.StopCommand.CanExecute(null));

        tool.StopCommand.Execute(null);
        Assert.Equal([("child-1", "call-1")], stops);
        Assert.True(tool.IsStopRequested);
        Assert.False(tool.CanStop);
        Assert.False(tool.StopCommand.CanExecute(null));
        Assert.Contains(nameof(LiveToolViewModel.CanStop), raised);
        Assert.Contains(nameof(LiveToolViewModel.IsStopRequested), raised);

        // A second press while the call winds down does not ask again.
        tool.StopCommand.Execute(null);
        Assert.Single(stops);
    }

    [Fact]
    public void A_stop_the_session_refused_leaves_the_row_stoppable()
    {
        // False: the call already finished (the race lost) or the session is gone; the row does not claim it is stopping.
        var asked = 0;
        var tool = Create(stop: (_, _) =>
        {
            asked++;
            return false;
        });

        tool.StopCommand.Execute(null);

        Assert.Equal(1, asked);
        Assert.False(tool.IsStopRequested);
        Assert.True(tool.CanStop);
    }

    [Fact]
    public void A_finished_call_or_a_row_without_a_stop_cannot_be_stopped()
    {
        var asked = 0;
        var tool = Create(stop: (_, _) =>
        {
            asked++;
            return true;
        });
        tool.Finish();
        Assert.False(tool.CanStop);
        Assert.False(tool.StopCommand.CanExecute(null));
        tool.StopCommand.Execute(null);
        Assert.Equal(0, asked);

        // A read-only session has no runtime, so its rows get no stop at all.
        var readOnly = new LiveToolViewModel("root", static () => null, new ToolCall("call-1", "Bash", "{}"), At, stopCall: null);
        Assert.False(readOnly.CanStop);
        Assert.False(readOnly.StopCommand.CanExecute(null));
    }

    [Fact]
    public void The_toggle_command_is_one_instance()
    {
        var tool = Create();
        Assert.Same(tool.ToggleCommand, tool.ToggleCommand);
    }

    [Fact]
    public void The_agent_label_names_the_root_a_childs_kind_and_a_child_without_one()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal("主 Agent", Create().AgentLabel);
        Assert.Equal("Scout", Create("child-1", () => "Scout").AgentLabel);
        Assert.Equal("子 Agent", Create("child-2", () => null).AgentLabel);
        Assert.Equal("子 Agent", Create("child-3", () => string.Empty).AgentLabel);
    }

    [Fact]
    public void The_row_starts_expanded_and_the_toggle_flips_the_glyph()
    {
        var tool = Create();
        var raised = new List<string?>();
        tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(tool.IsExpanded);
        Assert.Equal("\u25be", tool.ExpandGlyph);

        tool.ToggleCommand.Execute(null);

        Assert.False(tool.IsExpanded);
        Assert.Equal("\u25b8", tool.ExpandGlyph);
        Assert.Contains(nameof(LiveToolViewModel.IsExpanded), raised);
        Assert.Contains(nameof(LiveToolViewModel.ExpandGlyph), raised);
    }

    [Fact]
    public void A_language_switch_after_refresh_text_re_raises_the_label_and_the_elapsed_reading()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var tool = Create();
        var raised = new List<string?>();
        tool.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("主 Agent", tool.AgentLabel);

        Loc.Instance.Language = UiLanguage.English;
        tool.RefreshText();

        Assert.Equal("Main agent", tool.AgentLabel);
        Assert.Equal("0.0s", tool.ElapsedText);
        Assert.Contains(nameof(LiveToolViewModel.AgentLabel), raised);
        Assert.Contains(nameof(LiveToolViewModel.ElapsedText), raised);
    }

    private static int TailNotifications(List<string?> raised) =>
        raised.Count(static name => string.Equals(name, nameof(LiveToolViewModel.TailText), StringComparison.Ordinal));

    private static LiveToolViewModel Create(string agentId = "root", Func<string?>? agentKind = null, Func<string, string, bool>? stop = null) =>
        new(
            agentId,
            agentKind ?? (static () => null),
            new ToolCall("call-1", "Bash", """{"command":"npm test"}"""),
            At,
            stop ?? (static (_, _) => true));
}