using Forge.App.Composition;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary><c>provider.sendPromptCacheKey</c> and <c>provider.promptCacheTtl</c> as the snapshot resolves them (section 8).</summary>
public sealed class ProviderCacheSettingsTests
{
    [Fact]
    public void Absent_cache_settings_resolve_to_the_provider_defaults()
    {
        var snapshot = ProviderSnapshot.FromSection(Section());

        Assert.False(snapshot.SendPromptCacheKey);
        Assert.Null(snapshot.PromptCacheTtl);
    }

    [Theory]
    [InlineData("1h", "1h")]
    [InlineData(" 1H ", "1h")]
    [InlineData("5m", "5m")]
    [InlineData("", null)]
    public void Prompt_cache_ttl_is_normalised(string configured, string? expected)
    {
        var section = Section();
        section.PromptCacheTtl = configured;

        Assert.Equal(expected, ProviderSnapshot.FromSection(section).PromptCacheTtl);
    }

    [Fact]
    public void An_unknown_prompt_cache_ttl_is_a_format_error()
    {
        var section = Section();
        section.PromptCacheTtl = "24h";

        Assert.Throws<FormatException>(() => ProviderSnapshot.FromSection(section));
    }

    [Fact]
    public void Send_prompt_cache_key_is_read_from_the_section()
    {
        var section = Section();
        section.SendPromptCacheKey = true;

        Assert.True(ProviderSnapshot.FromSection(section).SendPromptCacheKey);
    }

    private static ProviderSection Section() => new()
    {
        Kind = "openai-compatible",
        BaseUrl = "https://example.invalid/v1/",
        Model = "model",
        ApiKey = "test-key-not-a-credential",
    };
}
