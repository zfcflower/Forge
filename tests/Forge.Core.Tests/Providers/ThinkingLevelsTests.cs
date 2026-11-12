using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins <see cref="ThinkingLevels"/>: the steps it reports come from the Pi catalogue snapshot, and a model nobody knows
/// gets <see cref="ThinkingEffort.Off"/> alone rather than every step, so the dial never offers a step the provider would
/// reject.
/// </summary>
public sealed class ThinkingLevelsTests
{
    private static readonly ThinkingEffort[] OffOnly = [ThinkingEffort.Off];

    private static readonly ThinkingEffort[] Standard =
        [ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High];

    /// <summary>Ids that must resolve to something the table or a family rule knows, used by the shape tests below.</summary>
    public static TheoryData<string> KnownIds => new()
    {
        "claude-opus-4-5",
        "claude-sonnet-4-5",
        "claude-haiku-4-5",
        "claude-opus-4-7",
        "claude-sonnet-5-5",
        "claude-sonnet-4-5-20250929",
        "gpt-5",
        "gpt-5-pro",
        "gpt-5.1",
        "gpt-5.2",
        "gpt-6-luna",
        "o3",
        "o4-mini",
        "gemini-2.5-pro",
        "gemini-3.1-pro-preview",
        "gemma-4-31b-it",
        "glm-4.6",
        "glm-5.2",
        "grok-4.3",
        "deepseek-flash",
        "deepseek-chat",
        "deepseek-reasoner",
        "k3",
        "kimi-k3",
        "kimi-k3.1-pro",
        "kimi-k2.7-code",
        "qwen3.8-max",
        "llama-4-scout",
        "mistral-medium-3",
        "some-private-finetune",
    };

    [Theory]
    [InlineData("k3", new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High, ThinkingEffort.Max })]
    [InlineData("kimi-k3", new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High, ThinkingEffort.Max })]
    [InlineData("kimi-k3.1-pro", new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High, ThinkingEffort.Max })]
    [InlineData("gpt-5-pro", new[] { ThinkingEffort.Off, ThinkingEffort.High })]
    [InlineData("glm-5.2", new[] { ThinkingEffort.Off, ThinkingEffort.High, ThinkingEffort.Max })]
    [InlineData("gemma-4-31b-it", new[] { ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.High })]
    [InlineData("o4-mini", new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High })]
    [InlineData(
        "grok-4.3",
        new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High, ThinkingEffort.XHigh })]
    [InlineData("qwen3.8-max", new[] { ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.XHigh })]
    [InlineData("deepseek-chat", new[] { ThinkingEffort.Off })]
    [InlineData("llama-4-scout", new[] { ThinkingEffort.Off })]
    public void Returns_exactly_the_steps_a_restricted_model_accepts(string modelId, ThinkingEffort[] expected) =>
        Assert.Equal(expected, ThinkingLevels.For(modelId));

    [Fact]
    public void Returns_every_step_for_a_model_the_catalogue_maps_to_all_of_them() =>
        AssertSteps(
            ThinkingLevels.For("claude-opus-5"),
            ThinkingEffort.Off,
            ThinkingEffort.Minimal,
            ThinkingEffort.Low,
            ThinkingEffort.Medium,
            ThinkingEffort.High,
            ThinkingEffort.XHigh,
            ThinkingEffort.Max);

    [Fact]
    public void Returns_the_five_default_steps_for_an_unmapped_reasoning_model()
    {
        Assert.Equal(Standard, ThinkingLevels.For("claude-opus-4-5"));
        Assert.Equal(Standard, ThinkingLevels.For("claude-sonnet-4-5"));
        Assert.Equal(Standard, ThinkingLevels.For("claude-haiku-4-5"));
        Assert.Equal(Standard, ThinkingLevels.For("gpt-5"));
        Assert.Equal(Standard, ThinkingLevels.For("gemini-2.5-pro"));
    }

    [Fact]
    public void Resolves_a_vendor_prefixed_id_to_the_same_steps_as_the_bare_one()
    {
        var prefixed = ThinkingLevels.For("moonshotai-cn/kimi-k3");

        Assert.Equal(ThinkingLevels.For("kimi-k3"), prefixed);
        AssertSteps(prefixed, ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High, ThinkingEffort.Max);
        Assert.Equal(ThinkingLevels.For("deepseek-flash"), ThinkingLevels.For("~deepseek/deepseek-flash"));
        Assert.Equal(Standard, ThinkingLevels.For("google/gemini-2.5-pro"));
    }

    [Fact]
    public void Resolves_a_dated_snapshot_to_the_entry_it_extends()
    {
        // Anthropic's API lists dated ids, so the remainder after a known id is the one place a prefix match is allowed.
        Assert.Equal(ThinkingLevels.For("claude-sonnet-4-5"), ThinkingLevels.For("claude-sonnet-4-5-20250929"));
        Assert.Equal(Standard, ThinkingLevels.For("claude-sonnet-4-5-20250929"));
        AssertSteps(
            ThinkingLevels.For("claude-opus-4-6-20260101"),
            ThinkingEffort.Off,
            ThinkingEffort.Minimal,
            ThinkingEffort.Low,
            ThinkingEffort.Medium,
            ThinkingEffort.High,
            ThinkingEffort.Max);

        // A dated snapshot the vendor re-released, and the "-latest" alias, are the same rule.
        Assert.Equal(
            ThinkingLevels.For("claude-sonnet-4-5"),
            ThinkingLevels.For("claude-sonnet-4-5-20250929-v2"));
        Assert.Equal(Standard, ThinkingLevels.For("claude-sonnet-4-5-latest"));
    }

    [Fact]
    public void A_suffix_that_is_not_a_snapshot_date_or_latest_is_a_different_model()
    {
        // Only "-yyyyMMdd[(-v<digits>)]" and "-latest" may follow a known id: a version bump, another date shape or extra
        // words name a model the snapshot does not know, which keeps Off alone instead of inheriting the base id's steps.
        Assert.Equal(OffOnly, ThinkingLevels.For("gpt-5.7"));
        Assert.Equal(OffOnly, ThinkingLevels.For("gpt-5.7-codex"));
        Assert.Equal(OffOnly, ThinkingLevels.For("gpt-5-2025"));
        Assert.Equal(OffOnly, ThinkingLevels.For("claude-sonnet-4-5-2025"));
        Assert.Equal(OffOnly, ThinkingLevels.For("claude-sonnet-4-5-experimental"));
        Assert.Equal(OffOnly, ThinkingLevels.For("claude-sonnet-4-5-20250929-v"));
    }

    [Fact]
    public void An_unknown_id_keeps_off_even_when_its_vendor_is_known()
    {
        // The counterpart of the exact and snapshot rules: recognising the vendor is not enough. A model the snapshot does
        // not list gets Off alone, because offering a step it may reject is the one thing the dial must never do.
        Assert.Equal(OffOnly, ThinkingLevels.For("claude-sonnet-9-9-unknown"));
        Assert.Equal(OffOnly, ThinkingLevels.For("gpt-9-imaginary"));
        Assert.Equal(OffOnly, ThinkingLevels.For("us.anthropic.claude-sonnet-5"));
        Assert.Equal(OffOnly, ThinkingLevels.For("amazon.nova-micro-v1:0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("unknown-model")]
    [InlineData("definitely-not-a-real-model-9000")]
    [InlineData("/")]
    [InlineData("vendor/")]
    [InlineData("moonshotai-cn/")]
    public void Returns_off_alone_for_an_id_it_does_not_know(string? modelId) =>
        Assert.Equal(OffOnly, ThinkingLevels.For(modelId));

    [Theory]
    [MemberData(nameof(KnownIds))]
    public void Every_known_model_gets_off_first_then_ascending_distinct_steps(string modelId)
    {
        var steps = ThinkingLevels.For(modelId);

        Assert.NotEmpty(steps);
        Assert.Equal(ThinkingEffort.Off, steps[0]);
        for (var index = 1; index < steps.Count; index++)
        {
            Assert.True(steps[index] > steps[index - 1], $"{modelId} lists {steps[index - 1]} before {steps[index]}");
        }

        for (var index = 0; index < steps.Count; index++)
        {
            for (var other = index + 1; other < steps.Count; other++)
            {
                Assert.NotEqual(steps[index], steps[other]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(KnownIds))]
    public void Lookup_is_case_insensitive_and_ignores_surrounding_whitespace(string modelId) =>
        Assert.Equal(ThinkingLevels.For(modelId), ThinkingLevels.For($"  {modelId.ToUpperInvariant()}\t"));

    private static void AssertSteps(IReadOnlyList<ThinkingEffort> actual, params ThinkingEffort[] expected) =>
        Assert.Equal(expected, actual);

    [Fact]
    public void Lookup_never_throws_and_never_returns_null()
    {
        string?[] inputs =
        [
            null, "", " ", "\t", "?", "🎛", new string('a', 4096), new string('/', 64), "vendor//model", "...", "-",
        ];

        foreach (var modelId in inputs)
        {
            Assert.NotNull(ThinkingLevels.For(modelId));
        }
    }
}
