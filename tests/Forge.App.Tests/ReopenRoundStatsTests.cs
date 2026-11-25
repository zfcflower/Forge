using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Reopening a session must show the same per-turn numbers the live session showed and leave the composer where the user left
/// it. Both come from outside the stored messages: the turn's clock, stream time, model and reasoning step are in
/// <c>turn_metrics</c>, and the chip's selection is written as it changes — a model switched and closed without sending
/// anything has no turn to be recovered from. The paged replay (commit "启动标签一次建齐，会话历史按页读取") replaced the
/// whole-history read and dropped the metrics seeding with it, so every reopened round read its clock and model as unknown.
/// </summary>
public sealed class ReopenRoundStatsTests : IDisposable
{
    private static readonly DateTimeOffset At0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-reopen-stats-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// The turn's wall clock, stream time, model and reasoning step are only in <c>turn_metrics</c> (the stored messages keep
    /// the tokens). A reopened session therefore has to seed each round from its row: before this test, the divider read "—"
    /// for the elapsed time, no output rate at all, and no model on the line or in its tooltip.
    /// </summary>
    [Fact]
    public async Task A_reopened_round_shows_the_elapsed_time_the_stream_time_and_the_model_it_recorded()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        var lastSeq = await store.AppendAsync(
            root.Id,
            ChatMessage.Assistant("first answer"),
            new TokenUsage(1_000, 1_200),
            CancellationToken.None);

        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id,
                1,
                At0,
                At0.AddSeconds(96.2),
                TurnMetricStatus.Completed,
                Provider: "probe",
                Model: "glm-5.3",
                ToolCalls: 3,
                StreamElapsed: TimeSpan.FromSeconds(12),
                Effort: ThinkingEffort.High,
                LastSeq: lastSeq),
            CancellationToken.None);

        var session = Reopen(root, store, metrics);
        await session.LoadHistoryAsync(CancellationToken.None);

        var divider = Assert.Single(session.Transcript.FlatRows().OfType<TurnDividerViewModel>());

        // The wall clock, the model and the reasoning step are the row's own values, not placeholders.
        Assert.Equal(TimeSpan.FromSeconds(96.2), divider.Card.Elapsed);
        Assert.Equal(ThinkingEffort.High, divider.Card.Effort);
        Assert.Equal("probe/glm-5.3:high", divider.Card.DividerModelText);

        // What the reader sees on the line: the elapsed segment, the rate the stream time produces (1,200 tokens over 12 s),
        // and the model in the standard provider/model:step form at the end. The tokens come from the stored message, the rate
        // from the metrics row's stream_ms, the label from its provider, model and effort columns.
        Assert.Contains(TextFormat.Elapsed(TimeSpan.FromSeconds(96.2)), divider.Line);
        Assert.Contains(TurnDividerText.FormatRate(100d), divider.Line);
        Assert.EndsWith(" · probe/glm-5.3:high", divider.Line, StringComparison.Ordinal);
        Assert.Contains("probe/glm-5.3:high", divider.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The composer starts from the provider's own default model (these tests wire no <c>settings.json</c> agent defaults),
    /// which says nothing about the session: a reopened tab has to land on the model and the reasoning step its last recorded
    /// turn ran, provider included, so the next turn continues the same way and the strip prices its window and spend with it.
    /// </summary>
    [Fact]
    public async Task A_reopened_session_puts_the_composer_back_on_the_model_and_step_its_last_turn_ran()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);

        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(root.Id, 1, At0, At0.AddSeconds(10), TurnMetricStatus.Completed, "probe", "model-a"),
            CancellationToken.None);
        await metrics.WriteAsync(
            new TurnMetric(
                root.Id,
                2,
                At0.AddMinutes(1),
                At0.AddMinutes(1).AddSeconds(10),
                TurnMetricStatus.Completed,
                "probe",
                "glm-5.3",
                Effort: ThinkingEffort.High),
            CancellationToken.None);

        var session = Reopen(root, store, metrics);

        // The provider's default model is what an unseeded composer shows, so the recovered model cannot be it by accident.
        Assert.Equal("model-a", session.Composer.Model);

        await session.LoadHistoryAsync(CancellationToken.None);

        // The last recorded turn is the one that ran the model the user closed the session on, and the step it ran with.
        Assert.Equal("glm-5.3", session.Composer.Model);
        Assert.Equal("probe", session.Composer.ProviderId);
        Assert.Equal(ThinkingEffort.High, session.Composer.Effort);
    }

    /// <summary>
    /// The chip the user picked is the session's selection whether or not a turn followed it: a model and step switched and then
    /// closed without sending anything has no metrics row to be recovered from, so the selection is written as it changes and
    /// the reopen reads it back instead of falling back to the last recorded turn.
    /// </summary>
    [Fact]
    public async Task A_model_and_step_picked_after_the_last_turn_survive_the_reopen()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.User("first question"), null, CancellationToken.None);
        await store.AppendAsync(root.Id, ChatMessage.Assistant("first answer"), null, CancellationToken.None);

        // The last turn ran model-a: that is what a reopen without a stored selection would put the chip back on.
        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(root.Id, 1, At0, At0.AddSeconds(10), TurnMetricStatus.Completed, "probe", "model-a"),
            CancellationToken.None);

        await using var uiState = new SqliteUiStateStore(directory);
        var composerSelections = new SessionComposerStore(uiState);

        var first = Reopen(root, store, metrics, composerSelections);
        await first.LoadHistoryAsync(CancellationToken.None);
        Assert.Equal("model-a", first.Composer.Model);

        // No message is sent after the switch: nothing but the selection write can remember it.
        first.Composer.Model = "glm-5.3";
        first.Composer.Effort = ThinkingEffort.High;
        await first.ComposerSelectionSaved;
        first.Dispose();

        var reopened = Reopen(root, store, metrics, composerSelections);
        await reopened.LoadHistoryAsync(CancellationToken.None);

        Assert.Equal("glm-5.3", reopened.Composer.Model);
        Assert.Equal("probe", reopened.Composer.ProviderId);
        Assert.Equal(ThinkingEffort.High, reopened.Composer.Effort);
    }

    /// <summary>
    /// Paging back has to seed the older rounds the same way the first page does: they are replayed into a transcript of their
    /// own before they join the list, and a page that skipped the seeding would read "—" on every round it brings back — the
    /// same loss the first page had, one click later.
    /// </summary>
    [Fact]
    public async Task An_older_page_of_history_is_seeded_from_the_same_metrics_table()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        long? turnOneLastSeq = null;
        for (var turn = 1; turn <= 51; turn++)
        {
            await store.AppendAsync(root.Id, ChatMessage.User($"question {turn}"), null, CancellationToken.None);
            var seq = await store.AppendAsync(root.Id, ChatMessage.Assistant($"answer {turn}"), null, CancellationToken.None);
            if (turn == 1)
            {
                turnOneLastSeq = seq;
            }
        }

        // Only turn 1's row matters here: it is on the page that is not loaded yet, so the pairing has to happen there.
        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(root.Id, 1, At0, At0.AddSeconds(30), TurnMetricStatus.Completed, "probe", "model-b", LastSeq: turnOneLastSeq),
            CancellationToken.None);

        var session = Reopen(root, store, metrics);
        await session.LoadHistoryAsync(CancellationToken.None);
        Assert.True(session.HasOlderHistory);
        Assert.DoesNotContain(session.Transcript.FlatRows().OfType<TurnDividerViewModel>(), divider => divider.Card.Round == 1);

        await session.LoadOlderHistoryAsync(CancellationToken.None);

        var first = Assert.Single(
            session.Transcript.FlatRows().OfType<TurnDividerViewModel>(),
            divider => divider.Card.Round == 1);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Card.Elapsed);

        // The row names no step here: the label then stops at the plain reference instead of inventing one.
        Assert.Equal("probe/model-b", first.Card.DividerModelText);
    }

    /// <summary>
    /// An agent session is a tab of its own and its chip is restored the same way: the user may run it on another model than
    /// the root's, and closing and reopening that tab must not silently move it back to the default. Its history load is the
    /// same entry point, so it reads the same <c>ui_state</c> row and falls back to its own last turn.
    /// </summary>
    [Fact]
    public async Task An_agent_session_tab_recovers_its_chip_from_its_own_last_turn()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        var child = await store.CreateAsync(
            new SessionCreate(directory, "child") { ParentSessionId = root.Id },
            CancellationToken.None);

        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(
                child.Id,
                1,
                At0,
                At0.AddSeconds(10),
                TurnMetricStatus.Completed,
                "probe",
                "glm-5.3",
                Effort: ThinkingEffort.High),
            CancellationToken.None);

        var session = Reopen(child, store, metrics, agentSession: new AgentSessionLaunch(root, 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));
        await session.LoadHistoryAsync(CancellationToken.None);

        Assert.Equal("glm-5.3", session.Composer.Model);
        Assert.Equal("probe", session.Composer.ProviderId);
        Assert.Equal(ThinkingEffort.High, session.Composer.Effort);
    }

    /// <summary>A selection written by the tab before it closed wins over the last recorded turn, agent session included.</summary>
    [Fact]
    public async Task An_agent_session_keeps_the_pick_it_stored_even_with_no_turn_behind_it()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        var child = await store.CreateAsync(
            new SessionCreate(directory, "child") { ParentSessionId = root.Id },
            CancellationToken.None);

        await using var metrics = new SqliteTurnMetricsStore(directory);
        await metrics.WriteAsync(
            new TurnMetric(child.Id, 1, At0, At0.AddSeconds(10), TurnMetricStatus.Completed, "probe", "model-a"),
            CancellationToken.None);

        await using var uiState = new SqliteUiStateStore(directory);
        var selections = new SessionComposerStore(uiState);
        await selections.WriteAsync(
            child.Id,
            new SessionComposerSelection { Model = "glm-5.3", Provider = "probe", Effort = "high" },
            CancellationToken.None);

        var session = Reopen(child, store, metrics, selections, new AgentSessionLaunch(root, 1, IsDraft: false, Forge.Core.Prompts.BuiltInAgents.Worker));
        await session.LoadHistoryAsync(CancellationToken.None);

        Assert.Equal("glm-5.3", session.Composer.Model);
        Assert.Equal(ThinkingEffort.High, session.Composer.Effort);
    }

    /// <summary>
    /// A draft agent session has no stored row, and the tab id a draft uses is reused by the next draft under the same root. A
    /// selection written under it would therefore be read back by a draft the user never picked it in, so nothing is written
    /// until the first turn turns the draft into a real session.
    /// </summary>
    [Fact]
    public async Task An_agent_session_draft_writes_no_selection_under_its_temporary_id()
    {
        await using var store = await OpenStoreAsync();
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        var draftId = "agent-draft-" + root.Id + "-1";
        var draft = new SessionInfo(draftId, directory, "worker", At0);

        await using var metrics = new SqliteTurnMetricsStore(directory);
        await using var uiState = new SqliteUiStateStore(directory);
        var selections = new SessionComposerStore(uiState);

        var session = Reopen(draft, store, metrics, selections, new AgentSessionLaunch(root, 1, IsDraft: true, Forge.Core.Prompts.BuiltInAgents.Worker));
        Assert.True(session.Composer.RestoreSessionModel("glm-5.3", "probe"));
        await session.ComposerSelectionSaved;

        Assert.Null(await selections.ReadAsync(draftId, CancellationToken.None));
    }

    /// <summary>The session store and the metrics table share one <c>forge.db</c>, as they do in the app.</summary>
    private Task<SqliteSessionStore> OpenStoreAsync()
    {
        Directory.CreateDirectory(directory);
        return Task.FromResult(new SqliteSessionStore(directory));
    }

    /// <summary>
    /// Two models on one provider, defaulting to <c>model-a</c>: the composer's starting point for a reopened tab. <c>glm-5.3</c>
    /// is a model the thinking table gives real steps, so a step can be recorded and restored on it.
    /// </summary>
    private ProviderServices Providers()
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        providers.Store.Save(
            [
                new ProviderDefinition
                {
                    Id = "probe",
                    PresetId = "custom",
                    DisplayName = "Probe",
                    Protocol = ProviderProtocol.OpenAiCompatible,
                    BaseUrl = "https://example.invalid",
                    CredentialKind = ProviderCredentialKind.ApiKey,
                    Models = ["model-a", "glm-5.3"],
                    DefaultModel = "model-a",
                    Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
                },
            ],
            "probe",
            ContextWindowDefault.Empty,
            providers.Catalog.ModelCatalogSourceUrl);
        providers.Reload();
        Assert.True(providers.Catalog.IsConfigured);
        return providers;
    }

    /// <summary>The tab as restore builds it: a read-only view of the stored session, replayed through the app's metrics table.</summary>
    /// <remarks>The preferences are memory-only on purpose: the app's shared <c>composer.json</c> belongs to whoever runs the
    /// tests, and its default model would decide the very starting point this test asserts.</remarks>
    private SessionWorkspaceViewModel Reopen(
        SessionInfo info,
        SqliteSessionStore store,
        ITurnMetricsStore metrics,
        SessionComposerStore? composerSelections = null,
        AgentSessionLaunch? agentSession = null) =>
        new(
            info,
            runtime: null,
            checkpoints: null,
            store,
            Providers(),
            composerPreferences: new ComposerPreferences(),
            instrumentSources: new MetricsSources(metrics),
            agentSession: agentSession,
            sessionComposer: composerSelections);

    /// <summary>The app's sources with the turn-metrics table wired in; every other reading is the null object.</summary>
    private sealed class MetricsSources(ITurnMetricsStore metrics) : ISessionInstrumentSources
    {
        private readonly EmptySessionInstrumentSources empty = EmptySessionInstrumentSources.Instance;

        public bool McpFailed() => empty.McpFailed();

        public event Action? McpStateChanged
        {
            add { }
            remove { }
        }

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => empty.ModelMetadata(modelId, providerId);

        public string? PromptCacheTtl(string? providerId) => empty.PromptCacheTtl(providerId);

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null) =>
            empty.TurnHistoryAsync(sessionId, ct, maxTurnExclusive);

        public TurnMetricsRecorder? TurnMetricsRecorder => null;

        public ITurnMetricsStore? TurnMetrics => metrics;
    }
}
