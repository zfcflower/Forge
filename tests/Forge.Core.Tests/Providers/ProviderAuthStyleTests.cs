using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// The three credential shapes on the wire. <see cref="ProviderAuthStyle"/> decides which header carries the credential —
/// an API key is not the same thing as an OAuth token, and a gateway may want no provider credential at all — while
/// <see cref="ProviderConfig.Headers"/> always wins, because the caller named the header explicitly.
/// </summary>
public class ProviderAuthStyleTests
{
    private const string ApiKey = "sk-ant-api-key";
    private const string SubscriptionToken = "sk-ant-oat01-subscription";

    [Fact]
    public async Task Anthropic_default_sends_the_api_key_header()
    {
        var handler = AnthropicHandler();

        await CollectAnthropic(handler, Anthropic(ProviderAuthStyle.Default));

        var request = handler.Last;
        Assert.Equal(ApiKey, request.Header("x-api-key"));
        Assert.Equal(AnthropicProvider.Version, request.Header("anthropic-version"));
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Anthropic_bearer_only_sends_the_token_and_the_beta_headers()
    {
        var handler = AnthropicHandler();

        await CollectAnthropic(handler, Anthropic(ProviderAuthStyle.BearerOnly, apiKey: SubscriptionToken));

        var request = handler.Last;
        Assert.Equal("Bearer " + SubscriptionToken, request.Header("Authorization"));
        Assert.Equal(AnthropicProvider.Version, request.Header("anthropic-version"));
        Assert.Equal(
            "claude-code-20250219,oauth-2025-04-20,fine-grained-tool-streaming-2025-05-14",
            request.Header("anthropic-beta"));
        Assert.Equal(string.Join(',', ClaudeSubscriptionCredentials.Betas), request.Header("anthropic-beta"));
        Assert.Null(request.Header("x-api-key"));
    }

    [Fact]
    public async Task Anthropic_none_leaves_the_credential_to_the_callers_headers()
    {
        var handler = AnthropicHandler();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Gateway-Key"] = "gw" };

        await CollectAnthropic(handler, Anthropic(ProviderAuthStyle.None, apiKey: null, headers: headers));

        var request = handler.Last;
        Assert.Null(request.Header("x-api-key"));
        Assert.Null(request.Header("Authorization"));
        Assert.Equal("gw", request.Header("X-Gateway-Key"));
        Assert.Equal(AnthropicProvider.Version, request.Header("anthropic-version"));
    }

    [Fact]
    public async Task Anthropic_callers_headers_override_the_provider_header()
    {
        var handler = AnthropicHandler();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer gateway-token",
            ["anthropic-beta"] = "gateway-beta",
        };

        await CollectAnthropic(handler, Anthropic(ProviderAuthStyle.BearerOnly, headers: headers));

        var request = handler.Last;
        Assert.Equal("Bearer gateway-token", request.Header("Authorization"));
        Assert.Equal("gateway-beta", request.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Anthropic_puts_the_identity_preamble_in_its_own_first_system_block()
    {
        var handler = AnthropicHandler();
        var config = Anthropic(
            ProviderAuthStyle.BearerOnly,
            apiKey: SubscriptionToken,
            preamble: ClaudeSubscriptionCredentials.IdentityPreamble);

        await CollectAnthropic(handler, config, TestData.Request(system: "forge-system-prompt", cache: new CacheHint(true)));

        using var document = handler.Last.Json();
        var system = document.RootElement.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);
        Assert.Equal(2, system.GetArrayLength());

        var preamble = system[0];
        Assert.Equal("text", preamble.GetProperty("type").GetString());
        Assert.Equal(ClaudeSubscriptionCredentials.IdentityPreamble, preamble.GetProperty("text").GetString());
        Assert.False(preamble.TryGetProperty("cache_control", out _));

        var prompt = system[1];
        Assert.Equal("text", prompt.GetProperty("type").GetString());
        Assert.Equal("forge-system-prompt", prompt.GetProperty("text").GetString());
        Assert.Equal("ephemeral", prompt.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Anthropic_without_a_preamble_keeps_one_system_block()
    {
        var handler = AnthropicHandler();

        await CollectAnthropic(handler, Anthropic(ProviderAuthStyle.Default), TestData.Request(cache: new CacheHint(true)));

        using var document = handler.Last.Json();
        var system = document.RootElement.GetProperty("system");
        Assert.Equal(1, system.GetArrayLength());
        Assert.Equal("system-prompt", system[0].GetProperty("text").GetString());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(ProviderAuthStyle.Default)]
    [InlineData(ProviderAuthStyle.BearerOnly)]
    public async Task OpenAi_sends_a_bearer_token_by_default(ProviderAuthStyle style)
    {
        var handler = OpenAiHandler();

        await CollectOpenAi(handler, OpenAi(style));

        Assert.Equal("Bearer sk-openai", handler.Last.Header("Authorization"));
    }

    [Fact]
    public async Task OpenAi_none_sends_no_auth_header()
    {
        var handler = OpenAiHandler();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Gateway-Key"] = "gw" };

        await CollectOpenAi(handler, OpenAi(ProviderAuthStyle.None, headers: headers));

        var request = handler.Last;
        Assert.Null(request.Header("Authorization"));
        Assert.Equal("gw", request.Header("X-Gateway-Key"));
    }

    private static FakeHttpMessageHandler AnthropicHandler() =>
        new FakeHttpMessageHandler().EnqueueSse(TestData.WithEvent("message_stop", """{"type":"message_stop"}"""));

    private static FakeHttpMessageHandler OpenAiHandler() =>
        new FakeHttpMessageHandler().EnqueueSse(TestData.Sse("[DONE]"));

    private static ProviderConfig Anthropic(
        ProviderAuthStyle style,
        string? apiKey = ApiKey,
        IReadOnlyDictionary<string, string>? headers = null,
        string? preamble = null) =>
        new(
            "anthropic",
            ProviderFactory.AnthropicKind,
            "https://api.anthropic.test",
            apiKey,
            headers,
            style,
            preamble);

    private static ProviderConfig OpenAi(
        ProviderAuthStyle style,
        string? apiKey = "sk-openai",
        IReadOnlyDictionary<string, string>? headers = null) =>
        new("openai", ProviderFactory.OpenAiCompatibleKind, "https://api.openai.test/v1", apiKey, headers, style);

    private static async Task CollectAnthropic(
        FakeHttpMessageHandler handler,
        ProviderConfig config,
        ChatRequest? request = null)
    {
        var provider = new AnthropicProvider(config, handler.CreateClient());
        await provider.CollectAsync(request ?? TestData.Request());
    }

    private static async Task CollectOpenAi(FakeHttpMessageHandler handler, ProviderConfig config)
    {
        var provider = new OpenAiCompatibleProvider(config, handler.CreateClient());
        await provider.CollectAsync(TestData.Request());
    }
}
