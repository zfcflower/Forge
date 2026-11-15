using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// Pins the composer strip's session usage summary (wave W16a, L4): the totals are the strip's own session usage (the stored
/// message usage seeded when the history was replayed, plus every turn this window watched), the turn count and the elapsed
/// time come from the session's turn checkpoints, and the means are the totals over those turns. Covered here: one live
/// turn accumulated into the totals and the means, a reopened session whose tokens come from the store and whose turns come
/// from a checkpoint database written before the window existed, a session without any checkpoints (only the turns this
/// window watched), an empty session, a model with no price, and the localized rows themselves.
/// </summary>
public sealed class UsageSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_finished_turn_accumulates_into_the_totals_and_the_means()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources { History = new SessionTurnHistory(2, TimeSpan.FromSeconds(90)) };
        using var instruments = Create(sources);
        sources.Settle();

        Assert.Equal("2", instruments.UsageTurnsText);
        Assert.Equal("1 分 30 秒", instruments.UsageDurationText);

        // Two recorded turns with nothing spent against them is not "no usage": the turn column has something to say.
        Assert.True(instruments.HasUsageSummary);

        // Two turns this window watched: the root agent's deltas land on the seeded history.
        instruments.AddTurn("root", new TokenUsage(1_000, 200, CacheReadTokens: 800));
        instruments.AddTurn("root", new TokenUsage(3_000, 400, CacheReadTokens: 2_200));

        Assert.True(instruments.HasUsageSummary);
        Assert.False(instruments.IsUsageSummaryEmpty);
        Assert.Equal("4k", instruments.UsageInputText);
        Assert.Equal("600", instruments.UsageOutputText);
        Assert.Equal("3k", instruments.UsageCacheReadText);
        Assert.Equal("0", instruments.UsageCacheWriteText);
        Assert.Equal("75.0%", instruments.UsageCacheHitText);

        // 2 recorded turns and 2 watched ones divide the totals, which is what the mean columns report.
        Assert.Equal(4, instruments.UsageTurnCount);
        Assert.Equal("1k", instruments.UsageAverageInputText);
        Assert.Equal("150", instruments.UsageAverageOutputText);
        Assert.Equal(
            UsageStrings.TurnsValueText("2", "2"),
            instruments.UsageTurnsText);
    }

    [Fact]
    public void A_sub_agent_turn_does_not_move_the_session_summary()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources { History = new SessionTurnHistory(1, TimeSpan.FromSeconds(5)) };
        using var instruments = Create(sources);
        sources.Settle();

        // The strip reports the root agent: a child's spend stays on its own tab, and its turn is not a session turn.
        instruments.AddTurn("sub-1", new TokenUsage(9_000, 900));

        Assert.Equal(1, instruments.UsageTurnCount);
        Assert.Equal("0", instruments.UsageInputText);
        Assert.Equal("0", instruments.UsageOutputText);
    }

    [Fact]
    public async Task A_reopened_session_reads_its_tokens_and_turns_from_the_store()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var directory = TestTempFiles.NewDirectory("forge-usage-");
        try
        {
            // Two completed turns and a third that never finished, written before this window existed.
            await using (var before = new SqliteTurnCheckpointStore(directory))
            {
                await before.BeginAsync("session-1", 0, "D:/wt", Tree('a'), At, default);
                await before.CompleteAsync("session-1", 0, Tree('b'), At.AddSeconds(30), default);
                await before.BeginAsync("session-1", 1, "D:/wt", Tree('b'), At.AddMinutes(1), default);
                await before.CompleteAsync("session-1", 1, Tree('c'), At.AddMinutes(2), default);
                await before.BeginAsync("session-1", 2, "D:/wt", Tree('c'), At.AddMinutes(3), default);
            }

            // A fresh store is the restart: nothing is remembered in the process.
            await using var after = new SqliteTurnCheckpointStore(directory);
            var history = SessionTurnHistory.From(await after.ListAsync("session-1", default));

            Assert.Equal(3, history.Turns);
            // Only the turns that ended have a duration; the unfinished one contributes a turn and no time.
            Assert.Equal(TimeSpan.FromSeconds(90), history.Duration);

            var sources = new UsageSources { History = history };
            using var instruments = Create(sources);
            sources.Settle();

            // The stored message usage reaches the strip through SeedHistory, exactly as the replayed history does it.
            instruments.SeedHistory(new TokenUsage(12_000, 3_000, CacheReadTokens: 9_000));

            Assert.Equal("12k", instruments.UsageInputText);
            Assert.Equal("3k", instruments.UsageOutputText);
            Assert.Equal("75.0%", instruments.UsageCacheHitText);
            Assert.Equal("3", instruments.UsageTurnsText);
            Assert.Equal("1 分 30 秒", instruments.UsageDurationText);
            Assert.Equal(3, instruments.UsageTurnCount);
            Assert.Equal("4k", instruments.UsageAverageInputText);
        }
        finally
        {
            TestTempFiles.DeleteTree(directory);
        }
    }

    [Fact]
    public void A_session_without_checkpoints_counts_only_the_turns_this_window_watched()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources();
        using var instruments = Create(sources);
        sources.Settle();

        Assert.True(instruments.HasTurnHistory);
        Assert.Equal("0", instruments.UsageTurnsText);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageDurationText);

        // A scratch session records no checkpoints, so the mean is still meaningful once a turn ran here.
        instruments.AddTurn("root", new TokenUsage(500, 100));

        Assert.Equal("1", instruments.UsageTurnsText);
        Assert.Equal("500", instruments.UsageAverageInputText);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageDurationText);
    }

    [Fact]
    public void An_empty_session_says_so_and_reports_no_invented_numbers()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        // No price for the model either: the two cost rows then have no number to show, and none is invented.
        var sources = new UsageSources { Metadata = ModelMetadata.Unknown };
        using var instruments = Create(sources);
        sources.Settle();

        Assert.True(instruments.IsUsageSummaryEmpty);
        Assert.False(instruments.HasUsageSummary);
        Assert.Equal(UsageStrings.EmptyText, instruments.UsageSummaryEmptyText);
        Assert.Equal("0", instruments.UsageInputText);
        Assert.Equal("0.0%", instruments.UsageCacheHitText);

        // Every row has a value, even the ones this session cannot fill: "—" is a reading, an empty cell a bug.
        var rows = instruments.UsageSummaryRows;
        Assert.Equal(11, rows.Count);
        Assert.Equal(UsageStrings.InputLabel, rows[0].Label);
        Assert.Equal(UsageStrings.CostLabel, rows[5].Label);
        Assert.Equal(UsageStrings.UnknownText, rows[5].Value);
        Assert.Equal(UsageStrings.AverageCostLabel, rows[10].Label);
        Assert.DoesNotContain(rows, static row => row.Value.Length == 0);
    }

    [Fact]
    public void A_model_without_a_price_reports_no_cost_and_a_language_switch_relabels_every_row()
    {
        var sources = new UsageSources { Metadata = ModelMetadata.Unknown };
        using var instruments = Create(sources);
        sources.Settle();
        instruments.AddTurn("root", new TokenUsage(2_000, 400, CacheReadTokens: 1_000));

        using (new LanguageScope(UiLanguage.Chinese))
        {
            Assert.Equal(UsageStrings.UnknownText, instruments.UsageCostText);
            Assert.Equal("每回合成本", instruments.UsageSummaryRows[10].Label);
        }

        using (new LanguageScope(UiLanguage.English))
        {
            // A language switch re-reads the rows instead of leaving the stale texts of the previous language behind.
            var labels = instruments.UsageSummaryRows.Select(static row => row.Label).ToList();
            Assert.Contains(UsageStrings.InputLabel, labels);
            Assert.Equal(UsageStrings.CostLabel, labels[5]);
            Assert.Equal(UsageStrings.TurnsLabel, labels[6]);
            Assert.Equal(UsageStrings.UnknownText, instruments.UsageCostText);
        }
    }

    [Fact]
    public void Cost_and_hit_ratio_follow_the_pricing_chain()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources();
        using var instruments = Create(sources);
        sources.Settle();

        // Billed input = input - cacheRead - cacheWrite: 10k - 6k - 1k = 3k at $3/M.
        instruments.AddTurn("root", new TokenUsage(10_000, 2_000, CacheReadTokens: 6_000, CacheWriteTokens: 1_000));

        // 6k/10k = 60.0%; cost = 3k*$3 + 6k*$0.30 + 1k*$3.75 + 2k*$15, all per million = $0.04455 -> $0.045.
        Assert.Equal("60.0%", instruments.UsageCacheHitText);
        Assert.Equal("$0.045", instruments.UsageCostText);
        Assert.Equal("$0.045", instruments.UsageAverageCostText);
        Assert.Equal("10k", instruments.UsageAverageInputText);
        Assert.Equal("2k", instruments.UsageAverageOutputText);
    }

    [Fact]
    public void Averages_without_turns_report_unknown_instead_of_zero()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources();
        using var instruments = Create(sources);
        sources.Settle();

        Assert.Equal(0, instruments.UsageTurnCount);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageAverageInputText);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageAverageOutputText);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageAverageCostText);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageDurationText);
    }

    [Fact]
    public void Seeded_history_plus_live_turns_share_one_total()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new UsageSources { History = new SessionTurnHistory(3, TimeSpan.FromMinutes(2)) };
        using var instruments = Create(sources);
        sources.Settle();

        instruments.SeedHistory(new TokenUsage(12_000, 3_000, CacheReadTokens: 9_000));
        instruments.AddTurn("root", new TokenUsage(4_000, 1_000, CacheReadTokens: 1_000));

        Assert.Equal("16k", instruments.UsageInputText);
        Assert.Equal("4k", instruments.UsageOutputText);
        Assert.Equal("10k", instruments.UsageCacheReadText);
        Assert.Equal(4, instruments.UsageTurnCount);
        Assert.Equal("4k", instruments.UsageAverageInputText);
        Assert.Equal("1k", instruments.UsageAverageOutputText);
        Assert.Equal(
            UsageStrings.TurnsValueText("3", "1"),
            instruments.UsageTurnsText);
    }

    [Fact]
    public void A_throwing_turn_history_leaves_recorded_unknown_but_keeps_live_turns()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var sources = new ThrowingTurnHistorySources();
        using var instruments = new SessionInstrumentsViewModel(
            Info(), sources, static () => Array.Empty<string>(), static () => "gpt-4o");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!instruments.HasTurnHistory && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.True(instruments.HasTurnHistory);
        Assert.Equal(0, instruments.UsageTurnCount);
        Assert.Equal(UsageStrings.UnknownText, instruments.UsageDurationText);

        instruments.AddTurn("root", new TokenUsage(500, 100));
        Assert.Equal(1, instruments.UsageTurnCount);
        Assert.Equal("1", instruments.UsageTurnsText);
        Assert.Equal("500", instruments.UsageAverageInputText);
    }

    [Fact]
    public void Turn_history_counts_a_backwards_clock_as_a_turn_without_time()
    {
        var ok = new TurnCheckpoint("s", 0, "w", Tree('a'), Tree('b'), At, At.AddSeconds(30));
        var unfinished = new TurnCheckpoint("s", 1, "w", Tree('b'), null, At.AddMinutes(1), null);
        var history = SessionTurnHistory.From([ok, unfinished]);
        Assert.Equal(2, history.Turns);
        Assert.Equal(TimeSpan.FromSeconds(30), history.Duration);

        var backwards = new TurnCheckpoint("s", 2, "w", Tree('c'), Tree('d'), At.AddMinutes(2), At.AddMinutes(1));
        var skewed = SessionTurnHistory.From([backwards]);
        Assert.Equal(1, skewed.Turns);
        Assert.Equal(TimeSpan.Zero, skewed.Duration);
    }

    [Fact]
    public async Task Stored_message_usage_survives_a_restart_with_identical_totals()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var directory = TestTempFiles.NewDirectory("forge-usage-store-");
        try
        {
            var first = new TokenUsage(7_000, 700, CacheReadTokens: 3_500);
            var second = new TokenUsage(5_000, 300, CacheReadTokens: 1_500, CacheWriteTokens: 200);
            await using (var before = new SqliteSessionStore(directory))
            {
                await before.AppendAsync("usage-1", UserText("q1"), null, default);
                await before.AppendAsync("usage-1", AssistantText("a1"), first, default);
                await before.AppendAsync("usage-1", UserText("q2"), null, default);
                await before.AppendAsync("usage-1", AssistantText("a2"), second, default);
            }

            await using var after = new SqliteSessionStore(directory);
            var messages = new List<StoredMessage>();
            await foreach (var stored in after.ReadAsync("usage-1", 1, null, default))
            {
                messages.Add(stored);
            }

            Assert.Equal(4, messages.Count);
            var replayed = TranscriptReplay.Apply(new TranscriptViewModel(collapseOlderTurns: false), messages);
            Assert.Equal(new TokenUsage(12_000, 1_000, 3_500 + 1_500, 200), replayed);

            // The reopened strip seeds the same sum the live window accumulated: one total, not two.
            var sources = new UsageSources { History = SessionTurnHistory.Empty };
            using var instruments = Create(sources);
            sources.Settle();
            instruments.SeedHistory(replayed);
            Assert.Equal("12k", instruments.UsageInputText);
            Assert.Equal("1k", instruments.UsageOutputText);
        }
        finally
        {
            TestTempFiles.DeleteTree(directory);
        }
    }

    [Fact]
    public void Corrupted_usage_json_is_ignored_instead_of_breaking_the_replay()
    {
        Assert.Null(MessageJson.ParseUsage("not-json"));
    }

    // ------------------------------------------------------------ helpers

    private static SessionInfo Info() => new("session-1", @"C:\work\forge", "usage", At);

    private static string Tree(char ch) => new(ch, 40);

    private static SessionInstrumentsViewModel Create(UsageSources sources) =>
        new(Info(), sources, static () => Array.Empty<string>(), static () => "gpt-4o");

    private static ChatMessage UserText(string text) =>
        new(MessageRole.User, [new TextBlock(text)]);

    private static ChatMessage AssistantText(string text) =>
        new(MessageRole.Assistant, [new TextBlock(text)]);

    /// <summary>A turn-history source that never answers: the strip must treat it as empty, not hang.</summary>
    private sealed class ThrowingTurnHistorySources : ISessionInstrumentSources
    {
        public bool HasVsCode => false;

        public int PendingApprovals(string sessionId) => 0;

        public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => Task.FromResult<int?>(0);

        public (int Ready, int Configured) McpServers() => (0, 0);

        public IReadOnlyList<SlashEntry> Skills(string workspacePath) => [];

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) =>
            new(128_000, new ModelPricing(3.00, 15.00, 0.30, 3.75));

        public void OpenInVsCode(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenSettingsSection(string sectionId)
        {
        }

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct) =>
            Task.FromException<SessionTurnHistory>(new InvalidOperationException("checkpoints unreadable"));
    }

    /// <summary>
    /// The instrument sources of one test session: a model with a known price (so cost and cache sums are exercised), no
    /// counters, and the turn history the test wants the strip to read. <see cref="Settle"/> pumps the one background read
    /// the strip starts in its constructor until it has landed.
    /// </summary>
    private sealed class UsageSources : ISessionInstrumentSources
    {
        public ModelMetadata Metadata { get; set; } = new(128_000, new ModelPricing(3.00, 15.00, 0.30, 3.75));

        public SessionTurnHistory History { get; set; } = SessionTurnHistory.Empty;

        public int HistoryReads { get; private set; }

        public bool HasVsCode => false;

        public int PendingApprovals(string sessionId) => 0;

        public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => Task.FromResult<int?>(0);

        public (int Ready, int Configured) McpServers() => (0, 0);

        public IReadOnlyList<SlashEntry> Skills(string workspacePath) => [];

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => Metadata;

        public void OpenInVsCode(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenSettingsSection(string sectionId)
        {
        }

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct)
        {
            HistoryReads++;
            return Task.FromResult(History);
        }

        /// <summary>
        /// Waits for the strip's background turn-history read. The fake answers synchronously, so this only has to let the
        /// continuation run; the loop turns a hang into a failed assertion instead of a hung test.
        /// </summary>
        public void Settle()
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (HistoryReads == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            Assert.True(HistoryReads > 0, "the strip never read the session's turn history");
        }
    }
}