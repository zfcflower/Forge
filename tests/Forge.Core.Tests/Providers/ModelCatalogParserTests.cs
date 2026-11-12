using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins what the app reads out of the models.dev payload: the fields it uses (<c>limit</c>, <c>cost</c>,
/// <c>modalities.input</c>, <c>reasoning</c>), and the entries it must drop or leave unknown rather than guess at.
/// </summary>
public sealed class ModelCatalogParserTests
{
    private const string Payload = """
    {
      "anthropic": {
        "id": "anthropic",
        "env": ["ANTHROPIC_API_KEY"],
        "npm": "@ai-sdk/anthropic",
        "name": "Anthropic",
        "models": {
          "claude-sonnet-4-5": {
            "id": "claude-sonnet-4-5",
            "limit": { "context": 200000, "output": 64000 },
            "cost": { "input": 3, "output": 15, "cache_read": 0.3, "cache_write": 3.75 },
            "modalities": { "input": ["text", "image", "pdf"], "output": ["text"] },
            "reasoning": true,
            "reasoning_options": [{ "type": "effort", "values": ["medium", "low", "high", "low"] }]
          },
          "claude-haiku-4-5": {
            "limit": { "context": 200000, "output": 64000 },
            "reasoning": false
          },
          "claude-opus-budget": {
            "reasoning": true,
            "reasoning_options": [{ "type": "budget_tokens", "min": 1024 }]
          },
          "legacy-embedding": { "name": "Only a name" },
          "half-priced": { "cost": { "input": 1 } },
          "tiered": {
            "limit": { "context": 1000000, "output": 32000 },
            "cost": { "input": 1.25, "output": 10, "context_over_200k": { "input": 2.5, "output": 20 } }
          }
        }
      },
      "no-models-object": { "id": "no-models-object", "name": "Broken" },
      "only-odd-models": {
        "models": {
          "strange": { "limit": "not an object", "cost": [], "modalities": { "input": "text" }, "reasoning": "yes" }
        }
      }
    }
    """;

    private static ModelCatalogSnapshot Parse(string payload, out string? error)
    {
        Assert.True(ModelCatalogParser.TryParse(Encoding.UTF8.GetBytes(payload), "https://models.dev/api.json", DateTimeOffset.UnixEpoch, "\"etag\"", out var snapshot, out error));
        return snapshot!;
    }

    [Fact]
    public void Reads_the_window_prices_inputs_and_steps()
    {
        var snapshot = Parse(Payload, out var error);
        Assert.Null(error);
        Assert.Equal("https://models.dev/api.json", snapshot.SourceUrl);
        Assert.Equal("\"etag\"", snapshot.ETag);

        var models = snapshot.Providers["anthropic"];
        var sonnet = models["claude-sonnet-4-5"];
        Assert.Equal(200_000, sonnet.ContextWindow);
        Assert.Equal(64_000, sonnet.MaxOutputTokens);
        Assert.Equal(new ModelPricing(3, 15, 0.3, 3.75), sonnet.Pricing);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, sonnet.Inputs);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High], sonnet.ReasoningSteps);
    }

    [Fact]
    public void A_model_the_source_marks_as_non_reasoning_offers_off_alone()
    {
        var haiku = Parse(Payload, out _).Providers["anthropic"]["claude-haiku-4-5"];

        Assert.Equal([ThinkingEffort.Off], haiku.ReasoningSteps);
        Assert.Null(haiku.Pricing);
        Assert.Null(haiku.Inputs);
    }

    [Fact]
    public void A_budget_only_reasoning_model_leaves_the_step_set_unknown()
    {
        // "It reasons, with a token budget" is not a set of steps; guessing one would offer a step the model may reject.
        var opus = Parse(Payload, out _).Providers["anthropic"]["claude-opus-budget"];

        Assert.Null(opus.ReasoningSteps);
        Assert.True(opus.Reasoning);
        Assert.True(opus.Capabilities?.Reasoning);
    }

    [Fact]
    public void An_entry_with_nothing_the_app_uses_is_dropped()
    {
        var models = Parse(Payload, out _).Providers["anthropic"];

        Assert.False(models.ContainsKey("legacy-embedding"));
        // A cost that names only input price is not a price: showing $0.00 output would read as "free".
        Assert.False(models.ContainsKey("half-priced"));
    }

    [Fact]
    public void Tiered_pricing_keeps_the_base_tier()
    {
        var tiered = Parse(Payload, out _).Providers["anthropic"]["tiered"];

        Assert.Equal(new ModelPricing(1.25, 10, 0, 0), tiered.Pricing);
        Assert.Null(tiered.Inputs);
    }

    [Fact]
    public void A_provider_without_models_is_skipped_and_odd_entries_do_not_fail_the_parse()
    {
        var snapshot = Parse(Payload, out _);

        Assert.False(snapshot.Providers.ContainsKey("no-models-object"));
        Assert.False(snapshot.Providers.ContainsKey("only-odd-models"));
        Assert.Equal(4, snapshot.ModelCount);
    }

    [Fact]
    public void An_empty_or_foreign_document_is_a_message_not_a_snapshot()
    {
        Assert.False(ModelCatalogParser.TryParse([], "https://models.dev/api.json", DateTimeOffset.UnixEpoch, null, out _, out var empty));
        Assert.Contains("empty", empty, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelCatalogParser.TryParse(Encoding.UTF8.GetBytes("[1,2]"), "u", DateTimeOffset.UnixEpoch, null, out _, out var notObject));
        Assert.Contains("object", notObject, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelCatalogParser.TryParse(Encoding.UTF8.GetBytes("{\"a\":1}"), "u", DateTimeOffset.UnixEpoch, null, out _, out var noModels));
        Assert.Contains("no models", noModels, StringComparison.OrdinalIgnoreCase);

        Assert.False(ModelCatalogParser.TryParse(Encoding.UTF8.GetBytes("{\"a\":"), "u", DateTimeOffset.UnixEpoch, null, out _, out var broken));
        Assert.Contains("JSON", broken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Metadata_from_an_entry_carries_the_capabilities()
    {
        var sonnet = Parse(Payload, out _).Providers["anthropic"]["claude-sonnet-4-5"];
        var metadata = sonnet.ToMetadata();

        Assert.Equal(200_000, metadata.ContextWindow);
        Assert.Equal(new ModelPricing(3, 15, 0.3, 3.75), metadata.Pricing);
        Assert.NotNull(metadata.Capabilities);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, metadata.Capabilities!.Inputs);
        Assert.True(metadata.IsKnown);
    }
}
