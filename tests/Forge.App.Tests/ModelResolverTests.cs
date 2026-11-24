using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The per-spawn model lookup: the text a user names resolves to the id a provider really lists, and anything the
/// lookup cannot settle comes back as the full candidate list instead of a guess. A wrong id would be a config that
/// only fails at request time, so every pass below is a contract.
/// </summary>
public sealed class ModelResolverTests
{
    private static readonly ProviderModels OpenCode = new(
        "opencode",
        "OPENCODE",
        ["kimi-k2", "kimi-k2.5", "claude-sonnet-4-5", "claude-opus-4-5", "qwen3.8-max-latest", "glm-5.3:free"]);

    private static readonly ProviderModels DeepSeek = new("deepseek", "DeepSeek API", ["deepseek-flash", "kimi-k2"]);

    private static ModelResolution Resolve(string text, string? provider = null) =>
        ModelResolver.Resolve(text, [OpenCode, DeepSeek], provider);

    [Fact]
    public void An_exact_id_wins_and_keeps_the_providers_spelling()
    {
        var result = Resolve("kimi-k2.5");

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("opencode", "kimi-k2.5"), result.Match);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void The_exact_pass_is_case_sensitive_so_a_literal_id_wins_over_another_spelling()
    {
        var providers = new ProviderModels[] { new("p", "P", ["abc", "ABC"]) };

        Assert.Equal(new ResolvedModel("p", "ABC"), ModelResolver.Resolve("ABC", providers).Match);
        Assert.Equal(new ResolvedModel("p", "abc"), ModelResolver.Resolve("abc", providers).Match);
    }

    [Theory]
    [InlineData("Kimi-K2.5")]                  // lower-case difference only
    [InlineData("moonshotai/kimi-k2.5")]       // a vendor/ prefix the provider does not spell
    public void Normalize_equality_matches_the_same_model_spelled_differently(string text)
    {
        var result = Resolve(text);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("opencode", "kimi-k2.5"), result.Match);
    }

    [Theory]
    [InlineData("glm-5.3", "glm-5.3:free")]
    [InlineData("qwen3.8-max", "qwen3.8-max-latest")]
    public void A_free_suffix_or_a_latest_suffix_is_the_same_model(string text, string listed)
    {
        var result = Resolve(text);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("opencode", listed), result.Match);
    }

    [Fact]
    public void A_snapshot_the_user_names_resolves_to_the_listed_base()
    {
        var providers = new ProviderModels[] { new("probe", "Probe", ["claude-sonnet-4-5"]) };

        var result = ModelResolver.Resolve("claude-sonnet-4-5-20250929", providers);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("probe", "claude-sonnet-4-5"), result.Match);
    }

    [Fact]
    public void A_listed_snapshot_resolves_from_the_base_the_user_names()
    {
        var providers = new ProviderModels[] { new("probe", "Probe", ["claude-sonnet-4-5-20250929"]) };

        var result = ModelResolver.Resolve("claude-sonnet-4-5", providers);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("probe", "claude-sonnet-4-5-20250929"), result.Match);
    }

    [Fact]
    public void A_family_word_resolves_through_the_containment_pass()
    {
        var result = Resolve("opus");

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("opencode", "claude-opus-4-5"), result.Match);
    }

    [Fact]
    public void The_same_id_on_two_providers_is_a_candidate_list_in_provider_order()
    {
        var result = Resolve("kimi-k2");

        Assert.Equal(ModelResolutionKind.Ambiguous, result.Kind);
        Assert.Null(result.Match);
        Assert.Equal(
            new[] { new ResolvedModel("opencode", "kimi-k2"), new ResolvedModel("deepseek", "kimi-k2") },
            result.Candidates);
    }

    [Fact]
    public void Several_listed_models_containing_the_text_are_all_candidates()
    {
        var result = Resolve("claude");

        Assert.Equal(ModelResolutionKind.Ambiguous, result.Kind);
        Assert.Equal(
            new[] { new ResolvedModel("opencode", "claude-sonnet-4-5"), new ResolvedModel("opencode", "claude-opus-4-5") },
            result.Candidates);
    }

    [Theory]
    [InlineData("deepseek")]   // provider id
    [InlineData("DEEPSEEK")]   // id, case-insensitive
    [InlineData("deep")]       // partial id
    [InlineData("DeepSeek API")] // display name
    public void A_provider_filter_narrows_the_candidates(string provider)
    {
        var result = Resolve("kimi-k2", provider);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("deepseek", "kimi-k2"), result.Match);
    }

    [Fact]
    public void A_provider_filter_that_names_nothing_is_reported_as_such()
    {
        var result = Resolve("kimi-k2", "nope");

        Assert.Equal(ModelResolutionKind.UnknownProvider, result.Kind);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("gpt-9")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_or_blank_text_is_never_guessed(string text)
    {
        var result = Resolve(text);

        Assert.Equal(ModelResolutionKind.UnknownModel, result.Kind);
        Assert.Null(result.Match);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void A_model_a_provider_lists_twice_yields_one_candidate()
    {
        var providers = new ProviderModels[] { new("p", "P", ["kimi-k2", "kimi-k2"]) };

        var result = ModelResolver.Resolve("kimi-k2", providers);

        Assert.Equal(ModelResolutionKind.Unique, result.Kind);
        Assert.Equal(new ResolvedModel("p", "kimi-k2"), result.Match);
    }

    [Fact]
    public void Suggest_ranks_the_real_id_of_a_reordered_name_and_ignores_the_unrelated_ones()
    {
        var providers = new ProviderModels[] { new("p", "P", ["gpt-6-luna", "luna-1-preview", "kimi-k2"]) };

        var suggestions = ModelResolver.Suggest("luna-6.1", providers);

        Assert.Equal(["gpt-6-luna", "luna-1-preview"], suggestions.Select(static suggestion => suggestion.Model));
    }

    [Fact]
    public void Suggest_says_nothing_when_no_word_is_shared()
    {
        var providers = new ProviderModels[] { new("p", "P", ["kimi-k2"]) };

        Assert.Empty(ModelResolver.Suggest("zulu", providers));
    }
}
