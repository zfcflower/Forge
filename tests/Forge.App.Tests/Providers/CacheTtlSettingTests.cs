using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Pins the cache-lifetime setting on a connected provider: which vendors get the control at all, that the choice round-trips through
/// <c>settings.json</c> without disturbing the rest of the entry, and that "auto" is stored as "the provider decides".
/// </summary>
public sealed class CacheTtlSettingTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-cache-ttl-");

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

    private static ProviderDefinition Anthropic() =>
        ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.AnthropicId)!, "anthropic");

    [Fact]
    public void Only_the_vendors_with_a_lifetime_in_their_protocol_offer_the_choice()
    {
        // Anthropic takes breakpoints with a lifetime; OpenAI takes a retention. Everything else caches on its own terms.
        Assert.True(Anthropic().SupportsPromptCacheTtl);
        Assert.True(ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.OpenAiId)!, "openai").SupportsPromptCacheTtl);
        Assert.False(ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.XAiId)!, "xai").SupportsPromptCacheTtl);
        Assert.False(ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.CopilotId)!, "copilot").SupportsPromptCacheTtl);
        Assert.False(ProviderDefinition.FromPreset(ProviderPresets.Find(ProviderPresets.KimiId)!, "kimi").SupportsPromptCacheTtl);
        Assert.False(ProviderDefinition.FromPreset(ProviderPresets.Custom, "custom").SupportsPromptCacheTtl);
    }

    [Theory]
    // The control writes "auto" as null ("no choice, the provider decides"), but a hand-written "auto" is kept as written: both are the
    // same policy, and rewriting a user's file to the other spelling would only make the diff noise.
    [InlineData("auto", "auto")]
    [InlineData(null, null)]
    [InlineData("5m", "5m")]
    [InlineData("1h", "1h")]
    public void The_choice_round_trips_through_the_provider_entry(string? chosen, string? stored)
    {
        var path = Path.Combine(root, "settings.json");
        var provider = Anthropic() with { PromptCacheTtl = chosen };
        new ProviderStore(path).Save(
            [provider],
            provider.Id,
            ContextWindowDefault.Empty);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entry = document.RootElement.GetProperty("providers").GetProperty("items")[0];
        if (stored is null)
        {
            // The provider entry writes null for every optional field it does not use, so "no choice" is a JSON null here.
            var value = entry.TryGetProperty("promptCacheTtl", out var property) ? property : default;
            Assert.True(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined, "auto is stored as no choice at all");
        }
        else
        {
            Assert.Equal(stored, entry.GetProperty("promptCacheTtl").GetString());
        }

        // Reading it back gives the same policy, which is what the loop consumes.
        var reloaded = ProviderDefinition.FromDocument(
            JsonSerializer.Deserialize<ProviderEntryDocument>(
                entry.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!,
            provider.Id);
        Assert.Equal(stored ?? "auto", reloaded.PromptCacheTtl ?? "auto");
    }
}
