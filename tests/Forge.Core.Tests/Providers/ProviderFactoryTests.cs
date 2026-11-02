using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

public class ProviderFactoryTests
{
    [Fact]
    public void Creates_openai_compatible_provider()
    {
        using var http = ProviderHttpClient.Create();
        var provider = ProviderFactory.Create(TestData.OpenAiConfig(), http);

        Assert.IsType<OpenAiCompatibleProvider>(provider);
        Assert.Equal("openai", provider.Id);
    }

    [Fact]
    public void Creates_anthropic_provider()
    {
        using var http = ProviderHttpClient.Create();
        var provider = ProviderFactory.Create(TestData.AnthropicConfig(), http);

        Assert.IsType<AnthropicProvider>(provider);
        Assert.Equal("anthropic", provider.Id);
    }

    [Theory]
    [InlineData("OpenAI-Compatible")]
    [InlineData("openai-compatible")]
    [InlineData(" openai-compatible ")]
    public void Matches_kind_case_insensitively(string kind)
    {
        using var http = ProviderHttpClient.Create();
        var provider = ProviderFactory.Create(new ProviderConfig("x", kind, "https://example.test", null), http);

        Assert.IsType<OpenAiCompatibleProvider>(provider);
    }

    [Fact]
    public void Rejects_unknown_kind()
    {
        using var http = ProviderHttpClient.Create();

        Assert.Throws<ArgumentException>(
            () => ProviderFactory.Create(new ProviderConfig("x", "gemini", "https://example.test", null), http));
    }
}
