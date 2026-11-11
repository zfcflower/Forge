using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// <c>sendPromptCacheKey</c> and <c>promptCacheTtl</c> as a connected provider resolves them. Ported from the single-provider
/// section: the values are per provider now, and a bad TTL degrades to the provider default instead of failing the whole list
/// (one hand-edited provider must not make the others unreadable).
/// </summary>
public sealed class ProviderCacheSettingsTests
{
    [Fact]
    public void Absent_cache_settings_resolve_to_the_provider_defaults()
    {
        var definition = ProviderDefinition.FromDocument(Entry(), "fallback");

        Assert.False(definition.SendPromptCacheKey);
        Assert.Null(definition.PromptCacheTtl);
    }

    [Theory]
    [InlineData("1h", "1h")]
    [InlineData(" 1H ", "1h")]
    [InlineData("5m", "5m")]
    [InlineData("", null)]
    [InlineData("24h", null)]
    public void Prompt_cache_ttl_is_normalised(string configured, string? expected)
    {
        var entry = Entry();
        entry.PromptCacheTtl = configured;

        Assert.Equal(expected, ProviderDefinition.FromDocument(entry, "fallback").PromptCacheTtl);
    }

    [Fact]
    public void Send_prompt_cache_key_is_read_from_the_entry()
    {
        var entry = Entry();
        entry.SendPromptCacheKey = true;

        Assert.True(ProviderDefinition.FromDocument(entry, "fallback").SendPromptCacheKey);
    }

    [Fact]
    public void Both_switches_round_trip_through_the_stored_document()
    {
        var definition = ProviderDefinition.FromDocument(Entry(), "fallback") with
        {
            SendPromptCacheKey = true,
            PromptCacheTtl = "1h",
        };

        var restored = ProviderDefinition.FromDocument(definition.ToDocument(), "fallback");

        Assert.True(restored.SendPromptCacheKey);
        Assert.Equal("1h", restored.PromptCacheTtl);
    }

    private static ProviderEntryDocument Entry() => new()
    {
        Id = "gateway",
        Preset = ProviderPresets.CustomId,
        Protocol = "openai-compatible",
        BaseUrl = "https://example.invalid/v1",
        Auth = new ProviderAuthDocument { Kind = "api-key", SecretReference = "provider:gateway" },
        Models = ["model"],
        DefaultModel = "model",
    };
}
