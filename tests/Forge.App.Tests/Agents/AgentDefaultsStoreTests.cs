using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// <see cref="AgentDefaultsStore"/> owns the <c>agents</c> section of <c>settings.json</c>: the file rules it shares with
/// <see cref="ProviderStore"/> (only that node is touched, a damaged file is refused), the seven stored thinking names, and
/// the resolution rule own entry → Worker → first connected model → no model.
/// </summary>
public sealed class AgentDefaultsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-agent-defaults-" + Guid.NewGuid().ToString("N"));

    public AgentDefaultsStoreTests() => Directory.CreateDirectory(root);

    private string SettingsPath => Path.Combine(root, "settings.json");

    [Fact]
    public void A_missing_file_loads_empty_and_never_throws()
    {
        var store = new AgentDefaultsStore(SettingsPath);

        Assert.Empty(store.All);
        Assert.Null(store.Get(AgentIds.Scout));
    }

    [Fact]
    public void A_missing_file_can_still_be_saved_and_creates_only_the_agents_node()
    {
        var store = new AgentDefaultsStore(SettingsPath);

        store.Save(AgentIds.Worker, new AgentDefault("alpha", "alpha-1", ThinkingEffort.Medium));

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var entry = document.RootElement.GetProperty("agents").GetProperty("worker");
        Assert.Equal("alpha", entry.GetProperty("providerId").GetString());
        Assert.Equal("alpha-1", entry.GetProperty("model").GetString());
        Assert.Equal("medium", entry.GetProperty("thinking").GetString());
        Assert.Equal(ThinkingEffort.Medium, new AgentDefaultsStore(SettingsPath).Get(AgentIds.Worker)!.Thinking);
    }

    [Fact]
    public void Every_stored_thinking_name_round_trips_as_a_lowercase_string()
    {
        var pairs = new (string Name, ThinkingEffort Effort)[]
        {
            ("off", ThinkingEffort.Off),
            ("minimal", ThinkingEffort.Minimal),
            ("low", ThinkingEffort.Low),
            ("medium", ThinkingEffort.Medium),
            ("high", ThinkingEffort.High),
            ("xhigh", ThinkingEffort.XHigh),
            ("max", ThinkingEffort.Max),
        };
        var store = new AgentDefaultsStore(SettingsPath);

        foreach (var (name, effort) in pairs)
        {
            store.Save(AgentIds.Plan, new AgentDefault(null, "alpha-1", effort));

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            Assert.Equal(name, document.RootElement.GetProperty("agents").GetProperty("plan").GetProperty("thinking").GetString());
            Assert.Equal(effort, new AgentDefaultsStore(SettingsPath).Get(AgentIds.Plan)!.Thinking);
        }
    }

    [Fact]
    public void An_unknown_stored_thinking_name_falls_back_to_off()
    {
        File.WriteAllText(
            SettingsPath,
            """{ "agents": { "scout": { "model": "m", "thinking": "ultra" } } }""");

        var store = new AgentDefaultsStore(SettingsPath);

        Assert.Equal(ThinkingEffort.Off, store.Get(AgentIds.Scout)!.Thinking);
    }

    [Fact]
    public void A_save_keeps_unknown_sections_unknown_agent_ids_and_unknown_entry_fields()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "preferences": { "language": "english" },
              "somethingThisBuildDoesNotKnow": { "keep": true },
              "agents": {
                "mystery": { "model": "future-model", "note": "kept" },
                "scout": { "model": "alpha-1", "note": "hand-written" }
              }
            }
            """);
        var before = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var unknownSection = before["somethingThisBuildDoesNotKnow"]!.ToJsonString();
        var unknownAgent = before["agents"]!["mystery"]!.ToJsonString();
        var unknownField = before["agents"]!["scout"]!["note"]!.ToJsonString();
        var store = new AgentDefaultsStore(SettingsPath);
        Assert.Null(store.Get("mystery"));

        store.Save(AgentIds.Scout, new AgentDefault("alpha", "alpha-1", ThinkingEffort.High));

        var after = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(unknownSection, after["somethingThisBuildDoesNotKnow"]!.ToJsonString());
        Assert.Equal(unknownAgent, after["agents"]!["mystery"]!.ToJsonString());
        Assert.Equal(unknownField, after["agents"]!["scout"]!["note"]!.ToJsonString());
        Assert.Equal("english", after["preferences"]!["language"]!.GetValue<string>());
        Assert.Equal("high", after["agents"]!["scout"]!["thinking"]!.GetValue<string>());
    }

    [Fact]
    public void Saving_null_removes_the_entry_and_keeps_the_other_agents()
    {
        File.WriteAllText(
            SettingsPath,
            """{ "agents": { "scout": { "model": "m" }, "plan": { "model": "p" } } }""");
        var store = new AgentDefaultsStore(SettingsPath);

        store.Save(AgentIds.Scout, null);

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var agents = document.RootElement.GetProperty("agents");
        Assert.False(agents.TryGetProperty("scout", out _));
        Assert.Equal("p", agents.GetProperty("plan").GetProperty("model").GetString());
        Assert.False(new AgentDefaultsStore(SettingsPath).All.ContainsKey(AgentIds.Scout));
    }

    [Fact]
    public void A_save_over_an_invalid_file_refuses_and_leaves_it_untouched()
    {
        const string broken = "{ not json";
        File.WriteAllText(SettingsPath, broken);
        var store = new AgentDefaultsStore(SettingsPath);

        Assert.Throws<InvalidOperationException>(() => store.Save(AgentIds.Main, new AgentDefault(null, "m", ThinkingEffort.Off)));
        Assert.Equal(broken, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_save_over_a_non_object_root_refuses_and_leaves_it_untouched()
    {
        const string array = "[1, 2, 3]";
        File.WriteAllText(SettingsPath, array);
        var store = new AgentDefaultsStore(SettingsPath);

        Assert.Throws<InvalidOperationException>(() => store.Save(AgentIds.Main, new AgentDefault(null, "m", ThinkingEffort.Off)));
        Assert.Equal(array, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_missing_or_broken_agents_node_loads_as_an_empty_map()
    {
        foreach (var json in new[]
                 {
                     """{ "agents": [] }""",
                     """{ "agents": "not an object" }""",
                     """{ "agents": { "scout": "not an entry", "plan": { "thinking": "high" } } }""",
                 })
        {
            File.WriteAllText(SettingsPath, json);

            var store = new AgentDefaultsStore(SettingsPath);

            Assert.Empty(store.All);
        }
    }

    [Fact]
    public void An_own_entry_wins_over_the_worker_entry()
    {
        File.WriteAllText(SettingsPath, TwoProviders(
            """
            "worker": { "model": "alpha-2", "thinking": "medium" },
            "scout": { "providerId": "beta", "model": "beta-1", "thinking": "xhigh" }
            """));
        var catalog = Services().Catalog;
        var store = new AgentDefaultsStore(SettingsPath);

        // The fixture's model ids are invented, so the resolution is asked with every step accepted: this test is about
        // which entry wins, not about what one model accepts (the model table has its own tests in Core).
        var scout = store.Resolve(AgentIds.Scout, catalog, EveryStep);
        Assert.NotNull(scout);
        Assert.Equal("beta-1", scout!.Model);
        Assert.Equal("beta", scout.ProviderId);
        Assert.Equal(ThinkingEffort.XHigh, scout.Thinking);

        var plan = store.Resolve(AgentIds.Plan, catalog, EveryStep);
        Assert.NotNull(plan);
        Assert.Equal("alpha-2", plan!.Model);
        Assert.Equal("alpha", plan.ProviderId);
        Assert.Equal(ThinkingEffort.Medium, plan.Thinking);

        var main = store.Resolve(AgentIds.Main, catalog, EveryStep);
        Assert.NotNull(main);
        Assert.Equal("alpha-2", main!.Model);
        Assert.Equal(ThinkingEffort.Medium, main.Thinking);
    }

    [Fact]
    public void Without_entries_the_first_model_of_the_first_connected_provider_runs_at_off()
    {
        File.WriteAllText(SettingsPath, TwoProviders(agents: string.Empty));
        var catalog = Services().Catalog;
        var store = new AgentDefaultsStore(SettingsPath);

        var main = store.Resolve(AgentIds.Main, catalog);
        Assert.NotNull(main);
        Assert.Equal("alpha-1", main!.Model);
        Assert.Equal("alpha", main.ProviderId);
        Assert.Equal(ThinkingEffort.Off, main.Thinking);
    }

    [Fact]
    public void Without_entries_the_first_connected_provider_wins_over_the_default_one()
    {
        // defaultId names the second stored provider, so the two rules differ: the fallback is the first listed provider (the
        // one the settings page shows first), not the default one a new session would start on.
        File.WriteAllText(SettingsPath, TwoProviders(agents: string.Empty, defaultId: "beta"));
        var catalog = Services().Catalog;
        var store = new AgentDefaultsStore(SettingsPath);

        Assert.Equal("beta", catalog.Default?.Id);

        var main = store.Resolve(AgentIds.Main, catalog);
        Assert.NotNull(main);
        Assert.Equal("alpha", main!.ProviderId);
        Assert.Equal("alpha-1", main.Model);
        Assert.Equal(ThinkingEffort.Off, main.Thinking);
    }

    [Fact]
    public void Without_a_connected_provider_an_agent_has_no_model()
    {
        File.WriteAllText(SettingsPath, """{ "providers": { "version": 1, "items": [] }, "agents": { "worker": { "model": "m" } } }""");
        var catalog = Services().Catalog;
        var store = new AgentDefaultsStore(SettingsPath);

        Assert.False(catalog.IsConfigured);
        Assert.Null(store.Resolve(AgentIds.Main, catalog));
        Assert.Null(store.Resolve(AgentIds.Worker, catalog));
    }

    [Fact]
    public void A_resolved_thinking_step_is_clamped_to_the_model_accepted_steps()
    {
        File.WriteAllText(SettingsPath, TwoProviders("\"plan\": { \"model\": \"beta-1\", \"thinking\": \"max\" }"));
        var catalog = Services().Catalog;
        var store = new AgentDefaultsStore(SettingsPath);

        static IReadOnlyList<ThinkingEffort> Restricted(string? model) =>
            string.Equals(model, "beta-1", StringComparison.Ordinal)
                ? [ThinkingEffort.Off, ThinkingEffort.Low]
                : ThinkingLevels.For(model);

        var resolved = store.Resolve(AgentIds.Plan, catalog, Restricted);

        Assert.NotNull(resolved);
        Assert.Equal(ThinkingEffort.Low, resolved!.Thinking);
        Assert.Equal("beta-1", resolved.Model);
    }

    private static IReadOnlyList<ThinkingEffort> EveryStep(string? _) =>
        [ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh, ThinkingEffort.Max];

    private ProviderServices Services()
    {
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        return new ProviderServices(SettingsPath, data, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static string TwoProviders(string agents, string defaultId = "alpha") =>
        """
        {
          "providers": { "version": 1, "defaultId": "{{DEFAULT}}", "items": [
            { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
              "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
              "models": ["alpha-1", "alpha-2"], "defaultModel": "alpha-1" },
            { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
              "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
              "models": ["beta-1"], "defaultModel": "beta-1" }
          ] },
          "agents": { {{AGENTS}} }
        }
        """.Replace("{{AGENTS}}", agents, StringComparison.Ordinal)
            .Replace("{{DEFAULT}}", defaultId, StringComparison.Ordinal);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
