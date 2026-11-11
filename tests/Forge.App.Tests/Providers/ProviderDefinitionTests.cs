using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// <see cref="ProviderDefinition"/> is the boundary between the stored document and the rest of the app: these pin the
/// round-trip, the repairs a hand-edited file gets, and the rule that a provider keeps working when its preset disappears.
/// </summary>
public sealed class ProviderDefinitionTests
{
    [Fact]
    public void A_definition_round_trips_through_the_stored_document()
    {
        var definition = new ProviderDefinition
        {
            Id = "kimi",
            PresetId = ProviderPresets.KimiId,
            DisplayName = "Kimi For Coding",
            Protocol = ProviderProtocol.Anthropic,
            BaseUrl = "https://api.kimi.com/coding",
            CredentialKind = ProviderCredentialKind.ApiKey,
            EnvironmentVariable = "KIMI_API_KEY",
            SecretReference = "provider:kimi",
            Models = ["k3", "k3-mini"],
            DefaultModel = "k3",
            Thinking = ThinkingEffort.Medium,
            MaxOutputTokens = 8192,
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["k3"] = new(1_048_576, new ModelPricing(0.5, 1.5, 0.1, 0.2)),
            },
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Gateway"] = "forge" },
        };

        var restored = ProviderDefinition.FromDocument(definition.ToDocument(), "fallback");

        Assert.Equal(definition.Id, restored.Id);
        Assert.Equal(definition.PresetId, restored.PresetId);
        Assert.Equal(definition.DisplayName, restored.DisplayName);
        Assert.Equal(definition.Protocol, restored.Protocol);
        Assert.Equal(definition.BaseUrl, restored.BaseUrl);
        Assert.Equal(definition.CredentialKind, restored.CredentialKind);
        Assert.Equal(definition.EnvironmentVariable, restored.EnvironmentVariable);
        Assert.Equal(definition.SecretReference, restored.SecretReference);
        Assert.Equal(definition.Models, restored.Models);
        Assert.Equal(definition.DefaultModel, restored.DefaultModel);
        Assert.Equal(definition.Thinking, restored.Thinking);
        Assert.Equal(definition.MaxOutputTokens, restored.MaxOutputTokens);
        Assert.Equal(definition.Overrides["k3"], restored.Overrides["k3"]);
        Assert.Equal("forge", restored.Headers["X-Gateway"]);
    }

    [Fact]
    public void A_default_model_missing_from_the_list_is_put_first()
    {
        var document = new ProviderEntryDocument
        {
            Id = "custom",
            Preset = ProviderPresets.CustomId,
            Protocol = "openai-compatible",
            BaseUrl = "https://example.invalid/v1",
            Models = ["a", "b"],
            DefaultModel = "pinned",
        };

        var definition = ProviderDefinition.FromDocument(document, "fallback");

        Assert.Equal(["pinned", "a", "b"], definition.Models);
        Assert.Equal("pinned", definition.DefaultModel);
    }

    [Fact]
    public void An_unknown_preset_degrades_to_the_custom_entry_instead_of_being_dropped()
    {
        var document = new ProviderEntryDocument
        {
            Id = "legacy",
            Preset = "vendor-that-no-longer-exists",
            Name = "Legacy gateway",
            Protocol = "openai-compatible",
            BaseUrl = "https://gateway.invalid/v1",
            Models = ["m"],
        };

        var definition = ProviderDefinition.FromDocument(document, "fallback");

        Assert.Equal(ProviderPresets.CustomId, ProviderPresets.FindOrDefault("vendor-that-no-longer-exists").Id);
        Assert.Equal("https://gateway.invalid/v1", definition.BaseUrl);
        Assert.Equal("Legacy gateway", definition.DisplayName);
        Assert.Equal("m", definition.DefaultModel);
    }

    [Fact]
    public void A_missing_id_falls_back_and_a_preset_less_entry_is_shown_as_custom()
    {
        var definition = ProviderDefinition.FromDocument(new ProviderEntryDocument(), "fallback-id");

        // A stored entry with no preset is not guessed into a vendor: it keeps its own (empty) URL and shows as the custom
        // entry, rather than silently pointing at Anthropic because that happens to be the product default.
        Assert.Equal("fallback-id", definition.Id);
        Assert.Equal(ProviderPresets.CustomId, definition.PresetId);
        Assert.Equal(string.Empty, definition.BaseUrl);
    }

    [Fact]
    public void An_empty_member_auth_makes_a_new_provider_take_the_presets_credential_kind()
    {
        var claude = ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.AnthropicId)!, "anthropic");
        var openAi = ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.OpenAiId)!, "openai");
        var copilot = ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.CopilotId)!, "copilot");

        Assert.Equal(ProviderCredentialKind.ClaudeSubscription, claude.CredentialKind);
        Assert.Equal(ProviderCredentialKind.ApiKey, openAi.CredentialKind);
        Assert.Equal(ProviderCredentialKind.GitHubCopilot, copilot.CredentialKind);
        Assert.Equal(CopilotAuthentication.DefaultApiHost, copilot.BaseUrl);
    }

    [Fact]
    public void Metadata_overrides_never_invent_a_zero()
    {
        var document = new Dictionary<string, ModelMetadataSection>(StringComparer.Ordinal)
        {
            ["unknown"] = new() { CacheRead = 0.2 },
            ["window"] = new() { ContextWindow = 128_000 },
            ["full"] = new() { ContextWindow = 1, Input = 1, Output = 2, CacheRead = 0.1, CacheWrite = 0.3 },
        };

        var resolved = ProviderDefinition.FromMetadataDocument(document);

        Assert.False(resolved.ContainsKey("unknown"), "a lone cache price is not a priced model, so the entry is dropped");
        Assert.Equal(128_000, resolved["window"].ContextWindow);
        Assert.Null(resolved["window"].Pricing);
        Assert.Equal(new ModelPricing(1, 2, 0.1, 0.3), resolved["full"].Pricing);
    }
}
