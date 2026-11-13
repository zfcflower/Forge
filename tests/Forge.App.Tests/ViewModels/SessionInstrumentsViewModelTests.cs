using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the composer strip's data (task spec "Forge.App", 计数语义 table): every counter is "this session's number over
/// the app-wide one", the context gauge shows a percentage only when the root agent reported its context size <em>and</em> the usable budget is known, and a
/// source that knows nothing leaves the readout blank instead of inventing a percentage or a price.
/// <para>The model metadata and the two background reads (skills, approval records) are faked through
/// <see cref="ISessionInstrumentSources"/>; the real chain is pinned in <c>ProviderMetadataTests</c> and the arithmetic
/// in the Core tests. Each test names the language it asserts in, and the one that switches the language restores it.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionInstrumentsViewModelTests
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How long a landed background read may take; the poll itself never blocks the UI thread.</summary>
    private const int PumpAttempts = 600;

    private const int PollMilliseconds = 5;

    // ---------------------------------------------------------------- the four counters

    [Fact]
    public async Task The_counters_read_this_sessions_number_over_the_app_wide_number()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources
        {
            Pending = 2,
            Records = 5,
            SkillList = [Skill("review"), Skill("docs"), Skill("test")],
            Mcp = (2, 4),
            Tools = ["read", "write", "bash", "grep"],
        };

        var state = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources);
                instruments.RefreshSources();
                var settled = await UntilAsync(
                    () => instruments.ApprovalText == "2/5" && instruments.SkillText == "0/3");
                return (
                    Settled: settled,
                    Approval: instruments.ApprovalText,
                    ApprovalTooltip: instruments.ApprovalTooltip,
                    Skill: instruments.SkillText,
                    SkillTooltip: instruments.SkillTooltip,
                    Mcp: instruments.McpText,
                    McpTooltip: instruments.McpTooltip,
                    Tool: instruments.ToolText,
                    ToolTooltip: instruments.ToolTooltip,
                    Labels: instruments.ApprovalsLabel + "|" + instruments.SkillsLabel + "|" +
                        instruments.McpLabel + "|" + instruments.ToolsLabel,
                    ApprovalsSession: sources.LastApprovalsSession,
                    SkillWorkspace: sources.LastSkillsWorkspace);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(state.Settled, "the background reads never reached the counters");
        Assert.Equal("2/5", state.Approval);
        Assert.Equal("本会话待审 2 条，审批库共 5 条记录", state.ApprovalTooltip);
        Assert.Equal("0/3", state.Skill);
        Assert.Equal("本会话用过 0 个技能，工作区与用户目录共发现 3 个", state.SkillTooltip);
        Assert.Equal("2/4", state.Mcp);
        Assert.Equal("2 台就绪 / 4 台已配置", state.McpTooltip);
        Assert.Equal("0/4", state.Tool);
        Assert.Equal("本会话调用过 0 种工具，可用 4 种", state.ToolTooltip);
        Assert.Equal("审批|技能|MCP|工具", state.Labels);

        // Both app-wide reads are scoped to this session and to its workspace, never to the whole app.
        Assert.Equal("session-1", state.ApprovalsSession);
        Assert.Equal(@"C:\work\forge", state.SkillWorkspace);
    }

    [Fact]
    public async Task An_unreadable_approvals_database_reads_as_zero_instead_of_a_stale_count()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Pending = 2, Records = 5 };

        var state = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources);
                instruments.RefreshSources();
                var known = await UntilAsync(() => instruments.ApprovalText == "2/5");

                sources.Records = null;
                instruments.RefreshSources();
                var unavailable = await UntilAsync(() => instruments.ApprovalText == "2/0");

                return (Known: known, Unavailable: unavailable, Text: instruments.ApprovalText, Tooltip: instruments.ApprovalTooltip);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(state.Known, "the first read never landed");
        Assert.True(state.Unavailable, "the unreadable database kept the previous record count");
        Assert.Equal("2/0", state.Text);
        Assert.Equal("本会话待审 2 条，审批库共 0 条记录", state.Tooltip);
    }

    // ---------------------------------------------------------------- tools

    [Fact]
    public async Task The_tool_counter_reads_zero_until_the_session_has_built_its_tool_list()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources();

        var state = await session.Dispatch(
            () =>
            {
                // A session that has not run yet: SessionRuntime.ToolNames() is empty until the first turn.
                using var instruments = Create(sources, availableTools: () => sources.Tools);
                instruments.NoteToolUsed("read");
                instruments.NoteToolUsed("bash");
                var beforeFirstTurn = (Text: instruments.ToolText, Tooltip: instruments.ToolTooltip);

                // The first turn built the agent host; the same two calls now have a denominator.
                sources.Tools = ["read", "write", "bash", "grep"];
                instruments.RefreshCounters();
                var afterFirstTurn = (Text: instruments.ToolText, Tooltip: instruments.ToolTooltip);
                return (Before: beforeFirstTurn, After: afterFirstTurn);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal("2/0", state.Before.Text);
        Assert.Equal("本会话调用过 2 种工具，可用 0 种", state.Before.Tooltip);
        Assert.Equal("2/4", state.After.Text);
        Assert.Equal("本会话调用过 2 种工具，可用 4 种", state.After.Tooltip);
    }

    [Fact]
    public async Task NoteToolUsed_counts_distinct_tool_names_only()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Tools = ["read", "write", "bash", "grep"] };

        var texts = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources, availableTools: () => sources.Tools);
                var before = instruments.ToolText;

                instruments.NoteToolUsed("read");
                instruments.NoteToolUsed("read");
                instruments.NoteToolUsed("bash");
                instruments.NoteToolUsed("BASH");
                instruments.NoteToolUsed(null);
                instruments.NoteToolUsed("   ");
                var after = instruments.ToolText;
                return (Before: before, After: after, Tooltip: instruments.ToolTooltip);
            },
            new CancellationTokenSource(StepBudget).Token);

        // Tool names are ordinal: "BASH" is a different tool from "bash" (the runtime's names are lower-case).
        Assert.Equal("0/4", texts.Before);
        Assert.Equal("3/4", texts.After);
        Assert.Equal("本会话调用过 3 种工具，可用 4 种", texts.Tooltip);
    }

    // ---------------------------------------------------------------- skills

    [Fact]
    public async Task NoteDraft_matches_a_slash_name_word_bounded_against_the_discovered_skills()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { SkillList = [Skill("review"), Skill("review-pr")] };

        var counts = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources);
                instruments.RefreshSources();
                var discovered = await UntilAsync(() => instruments.SkillText == "0/2");
                var found = instruments.SkillText;

                // "/reviewer" is a different word: the boundary after the name is what decides.
                instruments.NoteDraft("please /reviewer this diff");
                var notAWord = instruments.SkillText;

                // A name at the end of the draft is still a word.
                instruments.NoteDraft("/review");
                var atTheEnd = instruments.SkillText;

                // Names that were never discovered cannot be counted, and a name in prose still is.
                instruments.NoteDraft("/unknown-skill and /review-pr please");
                var withUnknown = instruments.SkillText;
                return (Discovered: discovered, Found: found, NotAWord: notAWord, AtTheEnd: atTheEnd,
                    WithUnknown: withUnknown, Tooltip: instruments.SkillTooltip);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(counts.Discovered, "the skill scan never landed");
        Assert.Equal("0/2", counts.Found);
        Assert.Equal("0/2", counts.NotAWord);
        Assert.Equal("1/2", counts.AtTheEnd);
        Assert.Equal("2/2", counts.WithUnknown);
        Assert.Equal("本会话用过 2 个技能，工作区与用户目录共发现 2 个", counts.Tooltip);
    }

    [Fact]
    public async Task A_skill_whose_name_starts_with_another_is_matched_by_its_own_slash_name()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { SkillList = [Skill("review"), Skill("review-pr")] };

        var counts = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources);
                instruments.RefreshSources();
                var discovered = await UntilAsync(() => instruments.SkillText == "0/2");

                // Only "/review-pr" is written here, so exactly one skill is used.
                instruments.NoteDraft("run /review-pr");
                var longer = instruments.SkillText;

                // If the first draft had counted "review", this one would add nothing.
                instruments.NoteDraft("run /review");
                var shorter = instruments.SkillText;
                return (Discovered: discovered, Longer: longer, Shorter: shorter);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(counts.Discovered, "the skill scan never landed");
        Assert.Equal("1/2", counts.Longer);
        Assert.Equal("2/2", counts.Shorter);
    }

    [Fact]
    public async Task A_draft_sent_before_the_skills_loaded_still_counts_afterwards()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { SkillList = [Skill("review"), Skill("docs")] };

        var counts = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources);

                // The first turn is sent before the discovery has landed: the draft is kept, and the denominator is
                // honest about knowing nothing yet (no skills discovered, so "0/0").
                instruments.NoteDraft("/review this change");
                var beforeDiscovery = instruments.SkillText;

                instruments.RefreshSources();
                var landed = await UntilAsync(() => instruments.SkillText == "1/2");
                return (BeforeDiscovery: beforeDiscovery, Landed: landed, After: instruments.SkillText);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal("0/0", counts.BeforeDiscovery);
        Assert.True(counts.Landed, "the draft recorded before the skill scan never counted against the discovered skills");
        Assert.Equal("1/2", counts.After);
    }

    // ---------------------------------------------------------------- usage and the context gauge

    [Fact]
    public async Task The_gauge_follows_the_root_agents_context_reports_and_ignores_turn_sums_and_child_agents()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Metadata = static _ => new ModelMetadata(200_000, null) };

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                var before = (instruments.HasContextMeasure, instruments.ContextLabel, instruments.ContextTooltip);

                // A turn's summed input is a cost figure, not a prompt size: the gauge stays where it was.
                instruments.AddTurn("root", new TokenUsage(90_000, 400));
                var afterTurn = (instruments.HasUsage, instruments.HasContextMeasure, instruments.ContextLabel);

                // A sub-agent has its own model and window; its report never reaches the session's gauge.
                instruments.NoteContext("child-1", 160_000, 170_000, 136_000);
                var afterChild = (instruments.HasContextMeasure, instruments.ContextLabel);

                var raised = Watch(instruments);
                instruments.NoteContext("root", 68_000, 170_000, 136_000);
                var measured = (
                    instruments.HasContextMeasure,
                    instruments.HasContextWindow,
                    instruments.ContextLabel,
                    instruments.ContextPercent,
                    instruments.ContextLevel,
                    instruments.ContextTooltip,
                    Segments: instruments.ContextSegments.Count,
                    Filled: instruments.ContextSegments.Count(static segment => segment.IsFilled));
                var names = string.Join(",", raised);

                return (Before: before, AfterTurn: afterTurn, AfterChild: afterChild, Measured: measured, Names: names);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.False(state.Before.HasContextMeasure);
        Assert.Equal("0%", state.Before.ContextLabel);
        Assert.Equal("上下文用量在下一次模型请求后显示。点击立即压缩", state.Before.ContextTooltip);

        Assert.True(state.AfterTurn.HasUsage);
        Assert.False(state.AfterTurn.HasContextMeasure);
        Assert.Equal("0%", state.AfterTurn.ContextLabel);

        Assert.False(state.AfterChild.HasContextMeasure);
        Assert.Equal("0%", state.AfterChild.ContextLabel);

        // C over U (the window minus the output reserve), not over the raw window.
        Assert.True(state.Measured.HasContextMeasure);
        Assert.True(state.Measured.HasContextWindow);
        Assert.Equal("68k/170k · 40%", state.Measured.ContextLabel);
        Assert.Equal(40d, state.Measured.ContextPercent);
        Assert.Equal(ContextLevel.Normal, state.Measured.ContextLevel);
        Assert.Equal("上下文 68k / 可用 170k（40%），到 136k（80%）自动压缩。点击立即压缩", state.Measured.ContextTooltip);
        Assert.Equal(5, state.Measured.Segments);
        Assert.Equal(2, state.Measured.Filled);

        foreach (var name in new[]
                 {
                     nameof(SessionInstrumentsViewModel.ContextLabel),
                     nameof(SessionInstrumentsViewModel.ContextPercent),
                     nameof(SessionInstrumentsViewModel.ContextSegments),
                     nameof(SessionInstrumentsViewModel.ContextTooltip),
                     nameof(SessionInstrumentsViewModel.IsContextWarning),
                     nameof(SessionInstrumentsViewModel.IsContextOver),
                 })
        {
            Assert.Contains(name, state.Names);
        }
    }

    [Fact]
    public async Task The_gauge_warns_ten_points_below_the_trigger_line_and_turns_over_at_it()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Metadata = static _ => new ModelMetadata(200_000, null) };

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                (ContextLevel Level, bool Warning, bool Over, string Label) Read() => (
                    instruments.ContextLevel,
                    instruments.IsContextWarning,
                    instruments.IsContextOver,
                    instruments.ContextLabel);

                // U 170k, trigger 80% = 136k: the warning starts at 70% = 119k.
                instruments.NoteContext("root", 118_999, 170_000, 136_000);
                var below = Read();
                instruments.NoteContext("root", 119_000, 170_000, 136_000);
                var warning = Read();
                instruments.NoteContext("root", 136_000, 170_000, 136_000);
                var over = Read();
                instruments.NoteContext("root", 200_000, 170_000, 136_000);
                var past = (Read(), instruments.ContextPercent, Filled: instruments.ContextSegments.Count(static s => s.IsFilled));

                // A compaction that reports no measurement keeps the last known budget and drops back to normal.
                instruments.NoteCompacted(17_000);
                var compacted = (Read(), instruments.HasContextWindow);
                return (Below: below, Warning: warning, Over: over, Past: past, Compacted: compacted);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal((ContextLevel.Normal, false, false), (state.Below.Level, state.Below.Warning, state.Below.Over));
        Assert.Equal((ContextLevel.Warning, true, false), (state.Warning.Level, state.Warning.Warning, state.Warning.Over));
        Assert.Equal("119k/170k · 70%", state.Warning.Label);
        Assert.Equal((ContextLevel.Over, false, true), (state.Over.Level, state.Over.Warning, state.Over.Over));

        // Past the usable budget the percentage reads the clamped 100 and every segment fills.
        Assert.Equal(ContextLevel.Over, state.Past.Item1.Level);
        Assert.Equal(100d, state.Past.ContextPercent);
        Assert.Equal(5, state.Past.Filled);

        Assert.Equal(ContextLevel.Normal, state.Compacted.Item1.Level);
        Assert.Equal("17k/170k · 10%", state.Compacted.Item1.Label);
        Assert.True(state.Compacted.HasContextWindow);
    }

    [Theory]
    [InlineData(0, 170_000, 136_000, nameof(ContextLevel.Normal))]
    [InlineData(118_999, 170_000, 136_000, nameof(ContextLevel.Normal))]
    [InlineData(119_000, 170_000, 136_000, nameof(ContextLevel.Warning))]
    [InlineData(135_999, 170_000, 136_000, nameof(ContextLevel.Warning))]
    [InlineData(136_000, 170_000, 136_000, nameof(ContextLevel.Over))]
    // A trigger line under ten points: the warning starts at zero.
    [InlineData(0, 100_000, 5_000, nameof(ContextLevel.Warning))]
    // Unknown budget or line: nothing to measure against.
    [InlineData(500_000, null, 136_000, nameof(ContextLevel.Normal))]
    [InlineData(500_000, 170_000, null, nameof(ContextLevel.Normal))]
    [InlineData(500_000, 0, 0, nameof(ContextLevel.Normal))]
    // Large values do not overflow the percentage arithmetic.
    [InlineData(int.MaxValue - 1, int.MaxValue, int.MaxValue, nameof(ContextLevel.Warning))]
    public void LevelOf_places_the_thresholds(int context, int? usable, int? trigger, string expected) =>
        Assert.Equal(expected, SessionInstrumentsViewModel.LevelOf(context, usable, trigger).ToString());

    [Fact]
    public async Task With_an_unknown_window_the_gauge_shows_tokens_only_and_never_warns()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Metadata = static _ => ModelMetadata.Unknown };

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                instruments.AddTurn("root", new TokenUsage(18_000, 400, CacheReadTokens: 9_000));

                // Core reports no budget for an unknown window; a non-positive budget is treated the same way.
                instruments.NoteContext("root", 18_000, null, null);
                var unknown = (
                    instruments.HasUsage,
                    instruments.HasContextMeasure,
                    instruments.HasContextWindow,
                    instruments.ContextLabel,
                    instruments.ContextPercent,
                    instruments.ContextLevel,
                    instruments.ContextTooltip,
                    Filled: instruments.ContextSegments.Count(static segment => segment.IsFilled),
                    instruments.UsageTooltip);

                instruments.NoteContext("root", 900_000, 0, 500);
                var zero = (instruments.HasContextWindow, instruments.ContextLabel, instruments.ContextLevel);
                return (Unknown: unknown, Zero: zero);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(state.Unknown.HasUsage);
        Assert.True(state.Unknown.HasContextMeasure);
        Assert.False(state.Unknown.HasContextWindow);
        Assert.Equal("18k", state.Unknown.ContextLabel);
        Assert.Equal(0d, state.Unknown.ContextPercent);
        Assert.Equal(ContextLevel.Normal, state.Unknown.ContextLevel);
        Assert.Equal(0, state.Unknown.Filled);
        Assert.Equal("上下文 18k；模型窗口未知，不显示百分比。点击立即压缩", state.Unknown.ContextTooltip);
        // Tokens and the cache ratio are still real numbers; the window, the percentage and the cost are not invented.
        Assert.Equal("↑18k ↓400 · R 9k · W 0 · RC 50.0% · $0.0000 · C 0", state.Unknown.UsageTooltip);

        Assert.False(state.Zero.HasContextWindow);
        Assert.Equal("900k", state.Zero.ContextLabel);
        Assert.Equal(ContextLevel.Normal, state.Zero.ContextLevel);
    }

    [Fact]
    public async Task The_gauge_text_follows_a_language_switch()
    {
        var session = SharedUi.Session;
        var previous = Loc.Instance.Language;
        var token = new CancellationTokenSource(StepBudget).Token;

        try
        {
            var state = await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = UiLanguage.Chinese;
                    var sources = new FakeSources { Metadata = static _ => new ModelMetadata(200_000, null) };
                    using var instruments = Create(sources);
                    instruments.NoteContext("root", 68_000, 170_000, 136_000);
                    var raised = Watch(instruments);
                    Loc.Instance.Language = UiLanguage.English;
                    return (Tooltip: instruments.ContextTooltip, Name: InstrumentsStrings.ContextCompactName, Names: string.Join(",", raised));
                },
                token);

            Assert.Equal("Context 68k of 170k usable (40%); compacts automatically at 136k (80%). Click to compact now", state.Tooltip);
            Assert.Equal("Compact context", state.Name);
            Assert.Contains(nameof(SessionInstrumentsViewModel.ContextTooltip), state.Names);
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    return 0;
                },
                token);
        }
    }


    [Fact]
    public async Task The_usage_tooltip_names_every_known_part_and_the_unknown_price_stays_out()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources
        {
            Metadata = static _ => new ModelMetadata(200_000, new ModelPricing(3.00, 15.00, 0.30, 3.75)),
        };

        var tooltip = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                instruments.AddTurn("root", new TokenUsage(18_000, 200, CacheReadTokens: 9_000));
                return instruments.UsageTooltip;
            },
            new CancellationTokenSource(StepBudget).Token);

        // ↑18k ↓200 · 9k read from cache · nothing written · 50.0% cache hit · $0.033 · window 200k
        // (9k billed input × $3, 9k cached × $0.30, 200 × $15).
        Assert.Equal("↑18k ↓200 · R 9k · W 0 · RC 50.0% · $0.033 · C 200k", tooltip);
    }

    [Fact]
    public async Task SeedHistory_fills_the_totals_without_inventing_a_context_percentage()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Metadata = static _ => new ModelMetadata(200_000, null) };

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                var empty = instruments.HasUsage;
                instruments.SeedHistory(TokenUsage.Empty);
                var afterEmpty = instruments.HasUsage;

                instruments.SeedHistory(new TokenUsage(5_000, 700, CacheReadTokens: 2_500));
                return (
                    Empty: empty,
                    AfterEmpty: afterEmpty,
                    HasUsage: instruments.HasUsage,
                    HasContextWindow: instruments.HasContextWindow,
                    ContextPercent: instruments.ContextPercent,
                    ContextLabel: instruments.ContextLabel,
                    Tooltip: instruments.UsageTooltip);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.False(state.Empty);
        Assert.False(state.AfterEmpty);
        Assert.True(state.HasUsage);

        // The prompt size of the session's last request is not recoverable from the stored sum.
        Assert.False(state.HasContextWindow);
        Assert.Equal(0d, state.ContextPercent);
        Assert.Equal("0%", state.ContextLabel);
        Assert.Equal("↑5k ↓700 · R 2.5k · W 0 · RC 50.0% · $0.0000 · C 200k", state.Tooltip);
    }

    // ---------------------------------------------------------------- git, project and the counters' commands

    [Fact]
    public async Task SetGit_raises_the_ahead_behind_marker()
    {
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(new FakeSources());
                var raised = Watch(instruments);
                var before = instruments.HasAheadBehind;

                instruments.SetGit("↑1 ↓2");
                var after = (instruments.AheadBehindText, instruments.HasAheadBehind);
                var names = string.Join(",", raised);

                raised.Clear();
                instruments.SetGit(null);
                var cleared = instruments.HasAheadBehind;
                var clearedNames = string.Join(",", raised);
                return (Before: before, After: after, Names: names, Cleared: cleared, ClearedNames: clearedNames);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.False(state.Before);
        Assert.Equal("↑1 ↓2", state.After.AheadBehindText);
        Assert.True(state.After.HasAheadBehind);
        foreach (var name in new[]
                 {
                     nameof(SessionInstrumentsViewModel.AheadBehindText),
                     nameof(SessionInstrumentsViewModel.HasAheadBehind),
                 })
        {
            Assert.Contains(name, state.Names);
        }

        Assert.False(state.Cleared);
        Assert.Contains(nameof(SessionInstrumentsViewModel.HasAheadBehind), state.ClearedNames);
    }

    [Fact]
    public async Task The_workspace_button_names_the_last_path_segment_and_keeps_the_full_path_in_its_tooltip()
    {
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(new FakeSources());
                return (instruments.WorkspaceName, instruments.WorkspaceTooltip, instruments.WorkspacePath);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal("forge", state.WorkspaceName);
        Assert.Contains(@"C:\work\forge", state.WorkspaceTooltip);
        Assert.Equal(@"C:\work\forge", state.WorkspacePath);
    }

    [Fact]
    public async Task SetProject_raises_the_project_properties_and_the_two_project_targets()
    {
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            () =>
            {
                var sources = new FakeSources();
                using var instruments = Create(sources);
                var raised = Watch(instruments);
                var explorer = 0;
                var vsCode = 0;
                instruments.OpenProjectInExplorerCommand.CanExecuteChanged += (_, _) => explorer++;
                instruments.OpenProjectInVsCodeCommand.CanExecuteChanged += (_, _) => vsCode++;

                var bare = (
                    instruments.HasProject,
                    instruments.ProjectName,
                    ExploresProject: instruments.OpenProjectInExplorerCommand.CanExecute(null),
                    OpensProjectInVsCode: instruments.OpenProjectInVsCodeCommand.CanExecute(null),
                    ExploresWorkspace: instruments.OpenWorkspaceInExplorerCommand.CanExecute(null));

                instruments.SetProject("forge", @"C:\src\forge");
                var named = (
                    instruments.HasProject,
                    instruments.ProjectName,
                    instruments.CanOpenProjectInExplorer,
                    instruments.CanOpenProjectInVsCode,
                    ExploresProject: instruments.OpenProjectInExplorerCommand.CanExecute(null),
                    OpensProjectInVsCode: instruments.OpenProjectInVsCodeCommand.CanExecute(null));
                var names = string.Join(",", raised);
                var afterFirst = (Explorer: explorer, VsCode: vsCode);

                // The same project again changes nothing, so it must not re-raise (the window pushes on every switch).
                instruments.SetProject("forge", @"C:\src\forge");
                var afterRepeat = (Explorer: explorer, VsCode: vsCode);

                // A project with no root cannot be opened anywhere, while the workspace target keeps working.
                instruments.SetProject("other", null);
                var withoutRoot = (
                    instruments.HasProject,
                    instruments.CanOpenProjectInExplorer,
                    instruments.CanOpenProjectInVsCode,
                    ExploresProject: instruments.OpenProjectInExplorerCommand.CanExecute(null),
                    ExploresWorkspace: instruments.OpenWorkspaceInExplorerCommand.CanExecute(null));
                return (Bare: bare, Named: named, Names: names, AfterFirst: afterFirst, AfterRepeat: afterRepeat,
                    WithoutRoot: withoutRoot);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.False(state.Bare.HasProject);
        Assert.Equal(string.Empty, state.Bare.ProjectName);
        Assert.False(state.Bare.ExploresProject);
        Assert.False(state.Bare.OpensProjectInVsCode);

        // The workspace target never depends on the project: the session always has a working directory.
        Assert.True(state.Bare.ExploresWorkspace);

        Assert.True(state.Named.HasProject);
        Assert.Equal("forge", state.Named.ProjectName);
        Assert.True(state.Named.CanOpenProjectInExplorer);
        Assert.True(state.Named.ExploresProject);
        Assert.False(state.Named.CanOpenProjectInVsCode);
        Assert.False(state.Named.OpensProjectInVsCode);

        foreach (var name in new[]
                 {
                     nameof(SessionInstrumentsViewModel.ProjectName),
                     nameof(SessionInstrumentsViewModel.HasProject),
                     nameof(SessionInstrumentsViewModel.CanOpenProjectInVsCode),
                     nameof(SessionInstrumentsViewModel.CanOpenProjectInExplorer),
                 })
        {
            Assert.Contains(name, state.Names);
        }

        Assert.Equal(1, state.AfterFirst.Explorer);
        Assert.Equal(1, state.AfterFirst.VsCode);
        Assert.Equal(state.AfterFirst, state.AfterRepeat);

        Assert.True(state.WithoutRoot.HasProject);
        Assert.False(state.WithoutRoot.CanOpenProjectInExplorer);
        Assert.False(state.WithoutRoot.ExploresProject);
        Assert.True(state.WithoutRoot.ExploresWorkspace);
    }

    [Fact]
    public async Task A_vscode_shim_makes_all_four_menu_entries_work_and_each_opens_its_own_folder()
    {
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            () =>
            {
                // The shim is resolved when the sources are built, so the fake knows about it from the start.
                var sources = new FakeSources { VSCode = true };
                using var instruments = Create(sources);
                instruments.SetProject("forge", @"C:\src\forge");
                var available = (
                    instruments.CanOpenProjectInVsCode,
                    instruments.CanOpenWorkspaceInVsCode,
                    ProjectInVsCode: instruments.OpenProjectInVsCodeCommand.CanExecute(null),
                    WorkspaceInVsCode: instruments.OpenWorkspaceInVsCodeCommand.CanExecute(null));

                instruments.OpenProjectInVsCodeCommand.Execute(null);
                var afterProject = string.Join(",", sources.OpenedIn);

                instruments.OpenWorkspaceInVsCodeCommand.Execute(null);
                var afterWorkspace = string.Join(",", sources.OpenedIn);

                instruments.OpenProjectInExplorerCommand.Execute(null);
                instruments.OpenWorkspaceInExplorerCommand.Execute(null);
                return (Available: available, AfterProject: afterProject, AfterWorkspace: afterWorkspace,
                    Revealed: string.Join(",", sources.Revealed));
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(state.Available.CanOpenProjectInVsCode);
        Assert.True(state.Available.CanOpenWorkspaceInVsCode);
        Assert.True(state.Available.ProjectInVsCode);
        Assert.True(state.Available.WorkspaceInVsCode);

        Assert.Equal(@"C:\src\forge", state.AfterProject);
        Assert.Equal(@"C:\src\forge,C:\work\forge", state.AfterWorkspace);
        Assert.Equal(@"C:\src\forge,C:\work\forge", state.Revealed);
    }

    [Fact]
    public async Task The_counters_open_their_own_settings_section()
    {
        var session = SharedUi.Session;
        var sources = new FakeSources();

        var opened = await session.Dispatch(
            () =>
            {
                using var instruments = Create(sources);
                instruments.OpenApprovalsCommand.Execute(null);
                instruments.OpenSkillsCommand.Execute(null);
                instruments.OpenMcpCommand.Execute(null);
                instruments.OpenToolsCommand.Execute(null);
                return string.Join(",", sources.OpenedSections);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal(
            string.Join(",", SettingsSectionIds.Approvals, SettingsSectionIds.Skills, SettingsSectionIds.Mcp, SettingsSectionIds.Tools),
            opened);
    }

    // ---------------------------------------------------------------- how much each refresh reads

    /// <summary>
    /// The two refresh entry points sit on different budgets (task design: <see cref="SessionInstrumentsViewModel"/> is
    /// re-read on every queue change and every turn start, so only one of them may touch a database or the file system).
    /// </summary>
    [Fact]
    public async Task RefreshCounters_reads_only_the_cheap_sources_while_RefreshSources_adds_the_two_slow_ones()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var sources = new FakeSources { Pending = 1, Records = 3, Tools = ["read", "write"], Mcp = (1, 2) };

        var state = await session.Dispatch(
            async () =>
            {
                using var instruments = Create(sources, availableTools: () => sources.Tools);

                // The constructor reads the approvals count once: it is one read-only query, and "0/0" for a session that
                // has simply no pending approvals reads worse than the truth. The workspace's skills stay unscanned.
                var constructed = await UntilAsync(() => sources.ApprovalReads == 1);
                var atConstruction = (sources.ApprovalReads, sources.SkillReads, instruments.ApprovalText);

                // Synchronous and cheap: the counters update, no database and no directory is touched.
                sources.Tools = ["read", "write", "bash"];
                instruments.RefreshCounters();
                var afterCounters = (
                    sources.ApprovalReads,
                    sources.SkillReads,
                    Tool: instruments.ToolText,
                    Mcp: instruments.McpText,
                    Approval: instruments.ApprovalText);

                instruments.RefreshSources();
                var firstLanded = await UntilAsync(() => sources.ApprovalReads == 2 && sources.SkillReads == 1);
                var afterSources = (sources.ApprovalReads, sources.SkillReads, instruments.ApprovalText, instruments.SkillText);

                // The approvals database is re-read every time; the skill scan runs once per session.
                instruments.RefreshSources();
                var secondLanded = await UntilAsync(() => sources.ApprovalReads == 3);
                var afterSecond = (sources.ApprovalReads, sources.SkillReads, instruments.SkillText);
                return (
                    AtConstruction: atConstruction,
                    AfterCounters: afterCounters,
                    FirstLanded: firstLanded,
                    AfterSources: afterSources,
                    SecondLanded: secondLanded,
                    AfterSecond: afterSecond);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal((1, 0), (state.AtConstruction.ApprovalReads, state.AtConstruction.SkillReads));
        Assert.Equal("1/3", state.AtConstruction.ApprovalText);

        Assert.Equal((1, 0), (state.AfterCounters.ApprovalReads, state.AfterCounters.SkillReads));
        Assert.Equal("0/3", state.AfterCounters.Tool);
        Assert.Equal("1/2", state.AfterCounters.Mcp);
        Assert.Equal("1/3", state.AfterCounters.Approval);

        Assert.True(state.FirstLanded, "RefreshSources never read the approvals database or the skills");
        Assert.Equal(2, state.AfterSources.ApprovalReads);
        Assert.Equal(1, state.AfterSources.SkillReads);
        Assert.Equal("1/3", state.AfterSources.ApprovalText);
        Assert.Equal("0/0", state.AfterSources.SkillText);

        Assert.True(state.SecondLanded, "a second RefreshSources did not re-read the approvals database");
        Assert.Equal(3, state.AfterSecond.ApprovalReads);
        Assert.Equal(1, state.AfterSecond.SkillReads);
    }

    // ---------------------------------------------------------------- language switch

    [Fact]
    public async Task A_language_switch_re_raises_the_labels_and_the_tooltips()
    {
        var session = SharedUi.Session;
        var previous = Loc.Instance.Language;
        var token = new CancellationTokenSource(StepBudget).Token;

        try
        {
            var state = await session.Dispatch(
                async () =>
                {
                    Loc.Instance.Language = UiLanguage.Chinese;
                    var sources = new FakeSources { Pending = 2, Records = 5, Tools = ["read"] };
                    using var instruments = Create(sources, availableTools: () => sources.Tools);
                    instruments.RefreshSources();
                    var settled = await UntilAsync(() => instruments.ApprovalText == "2/5");
                    var raised = Watch(instruments);

                    var chinese = (
                        instruments.ApprovalsLabel,
                        instruments.ToolsLabel,
                        instruments.ApprovalTooltip,
                        instruments.ResourcesTooltip,
                        instruments.ResourcesLabel);

                    Loc.Instance.Language = UiLanguage.English;
                    var names = string.Join(",", raised);
                    var english = (
                        instruments.ApprovalsLabel,
                        instruments.ToolsLabel,
                        instruments.ApprovalTooltip,
                        instruments.ResourcesTooltip,
                        instruments.ResourcesLabel);

                    // Still the same numbers, only the words changed.
                    var text = instruments.ApprovalText + " " + instruments.ToolText;
                    return (Settled: settled, Chinese: chinese, English: english, Names: names, Text: text);
                },
                token);

            Assert.True(state.Settled, "the approvals read never landed");
            Assert.Equal("审批", state.Chinese.ApprovalsLabel);
            Assert.Equal("工具", state.Chinese.ToolsLabel);
            Assert.Equal("本会话待审 2 条，审批库共 5 条记录", state.Chinese.ApprovalTooltip);
            Assert.Equal("资源", state.Chinese.ResourcesLabel);
            Assert.Contains("审批", state.Chinese.ResourcesTooltip, StringComparison.Ordinal);

            Assert.Equal("Approvals", state.English.ApprovalsLabel);
            Assert.Equal("Tools", state.English.ToolsLabel);
            Assert.Equal("2 waiting in this session, 5 records in the approvals database", state.English.ApprovalTooltip);
            Assert.Equal("Resources", state.English.ResourcesLabel);
            Assert.Contains("approvals", state.English.ResourcesTooltip, StringComparison.Ordinal);
            Assert.Equal("2/5 0/1", state.Text);

            foreach (var name in new[]
                     {
                         nameof(SessionInstrumentsViewModel.ApprovalsLabel),
                         nameof(SessionInstrumentsViewModel.SkillsLabel),
                         nameof(SessionInstrumentsViewModel.McpLabel),
                         nameof(SessionInstrumentsViewModel.ToolsLabel),
                         nameof(SessionInstrumentsViewModel.ResourcesTooltip),
                         nameof(SessionInstrumentsViewModel.ApprovalText),
                         nameof(SessionInstrumentsViewModel.ApprovalTooltip),
                     })
            {
                Assert.Contains(name, state.Names);
            }
        }
        finally
        {
            await session.Dispatch(
                () =>
                {
                    Loc.Instance.Language = previous;
                    return 0;
                },
                token);
        }
    }

    // ---------------------------------------------------------------- the null object

    [Fact]
    public async Task The_empty_sources_answer_zero_and_do_nothing()
    {
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            async () =>
            {
                ISessionInstrumentSources sources = EmptySessionInstrumentSources.Instance;
                var records = await sources.ApprovalRecordsAsync(CancellationToken.None);

                // None of these may throw on the null object.
                sources.OpenInVsCode(@"C:\src\forge");
                sources.OpenInExplorer(@"C:\src\forge");
                sources.OpenSettingsSection(SettingsSectionIds.Approvals);

                return (
                    Pending: sources.PendingApprovals("session-1"),
                    Records: records,
                    Mcp: sources.McpServers(),
                    Skills: sources.Skills(@"C:\work").Count,
                    Metadata: ReferenceEquals(sources.ModelMetadata("gpt-4o"), ModelMetadata.Unknown),
                    HasVsCode: sources.HasVsCode);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.Equal(0, state.Pending);
        Assert.Equal(0, state.Records);
        Assert.Equal((0, 0), state.Mcp);
        Assert.Equal(0, state.Skills);
        Assert.True(state.Metadata);
        Assert.False(state.HasVsCode);
    }

    [Fact]
    public async Task A_session_on_the_empty_sources_still_has_a_strip_whose_commands_do_nothing()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;

        var state = await session.Dispatch(
            async () =>
            {
                using var instruments = new SessionInstrumentsViewModel(
                    Info(),
                    EmptySessionInstrumentSources.Instance,
                    static () => [],
                    static () => "gpt-4o");

                // The constructor takes the cheap counters plus the one approvals query; the null object answers zero.
                var settled = await UntilAsync(() => instruments.ApprovalText == "0/0");
                var unread = instruments.ApprovalText;
                var counters = instruments.ApprovalText + " " + instruments.SkillText + " " +
                    instruments.McpText + " " + instruments.ToolText;

                // Every command is executable (nothing to open, nothing to count) and none may throw.
                instruments.OpenApprovalsCommand.Execute(null);
                instruments.OpenSkillsCommand.Execute(null);
                instruments.OpenMcpCommand.Execute(null);
                instruments.OpenToolsCommand.Execute(null);
                instruments.OpenProjectInVsCodeCommand.Execute(null);
                instruments.OpenWorkspaceInVsCodeCommand.Execute(null);
                instruments.OpenProjectInExplorerCommand.Execute(null);
                instruments.OpenWorkspaceInExplorerCommand.Execute(null);

                // Nothing to count against, but a call is still this session's own number.
                instruments.NoteToolUsed("read");
                instruments.NoteDraft("/review");
                return (
                    Unread: unread,
                    Settled: settled,
                    Counters: counters,
                    ToolAfterCall: instruments.ToolText,
                    Context: instruments.ContextLabel,
                    Project: instruments.HasProject);
            },
            new CancellationTokenSource(StepBudget).Token);

        Assert.True(state.Settled, "the null object's zero counts never landed");
        Assert.Equal("0/0", state.Unread);
        Assert.Equal("0/0 0/0 0/0 0/0", state.Counters);
        Assert.Equal("1/0", state.ToolAfterCall);
        Assert.Equal("0%", state.Context);
        Assert.False(state.Project);
    }

    // ---------------------------------------------------------------- helpers

    private static SessionInfo Info() => new("session-1", @"C:\work\forge", "instruments", At);

    private static SlashEntry Skill(string name) => new(name, name + " skill", IsSkill: true);

    private static SessionInstrumentsViewModel Create(
        FakeSources sources,
        Func<IReadOnlyList<string>>? availableTools = null) =>
        new(
            Info(),
            sources,
            availableTools ?? (() => sources.Tools),
            static () => "gpt-4o");

    /// <summary>Collects the property names a view model raises, so a refresh can be asserted and not just its values.</summary>
    private static List<string> Watch(SessionInstrumentsViewModel instruments)
    {
        var raised = new List<string>();
        instruments.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        return raised;
    }

    /// <summary>
    /// Waits for a background read to land: the continuation is posted to the headless dispatcher, so the wait yields
    /// instead of blocking it, and the assertions still read the state on one thread.
    /// </summary>
    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < PumpAttempts; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PollMilliseconds, CancellationToken.None);
        }

        return condition();
    }

    private sealed class FakeSources : ISessionInstrumentSources
    {
        private int approvalReads;
        private int skillReads;

        public int Pending { get; set; }

        public int? Records { get; set; } = 0;

        public (int Ready, int Configured) Mcp { get; set; }

        public IReadOnlyList<SlashEntry> SkillList { get; set; } = [];

        public IReadOnlyList<string> Tools { get; set; } = [];

        public bool VSCode { get; set; }

        public Func<string, ModelMetadata> Metadata { get; set; } = static _ => Forge.Abstractions.ModelMetadata.Unknown;

        public List<string> OpenedSections { get; } = [];

        public List<string> OpenedIn { get; } = [];

        public List<string> Revealed { get; } = [];

        public string? LastApprovalsSession { get; private set; }

        public string? LastSkillsWorkspace { get; private set; }

        /// <summary>How often the approvals database was read; the counter the "cheap refresh" rule is judged by.</summary>
        public int ApprovalReads => Volatile.Read(ref approvalReads);

        /// <summary>How often the skill directories were scanned; the scan must run once per session.</summary>
        public int SkillReads => Volatile.Read(ref skillReads);

        public bool HasVsCode => VSCode;

        public int PendingApprovals(string sessionId)
        {
            LastApprovalsSession = sessionId;
            return Pending;
        }

        public Task<int?> ApprovalRecordsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref approvalReads);
            return Task.FromResult(Records);
        }

        public (int Ready, int Configured) McpServers() => Mcp;

        public IReadOnlyList<SlashEntry> Skills(string workspacePath)
        {
            LastSkillsWorkspace = workspacePath;
            Interlocked.Increment(ref skillReads);
            return SkillList;
        }

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => Metadata(modelId);

        public void OpenInVsCode(string path) => OpenedIn.Add(path);

        public void OpenInExplorer(string path) => Revealed.Add(path);

        public void OpenSettingsSection(string sectionId) => OpenedSections.Add(sectionId);
    }
}
