using Forge.Abstractions;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Prompts;

public class PromptFingerprintTests
{
    private static readonly ToolDefinition Alpha = new("alpha", "First.", """{"type":"object"}""");
    private static readonly ToolDefinition Beta = new("beta", "Second.", """{"type":"object"}""");

    [Fact]
    public void Same_inputs_give_the_same_32_hex_digit_fingerprint()
    {
        var first = PromptFingerprint.Compute("system", [Alpha, Beta]);
        var second = PromptFingerprint.Compute(new string("system".AsSpan()), [Alpha with { }, Beta with { }]);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.Matches("^[0-9a-f]{32}$", first);
    }

    [Fact]
    public void Every_part_and_the_tool_order_change_the_fingerprint()
    {
        var baseline = PromptFingerprint.Compute("system", [Alpha, Beta]);

        Assert.NotEqual(baseline, PromptFingerprint.Compute("system!", [Alpha, Beta]));
        Assert.NotEqual(baseline, PromptFingerprint.Compute("system", [Beta, Alpha]));
        Assert.NotEqual(baseline, PromptFingerprint.Compute("system", [Alpha]));
        Assert.NotEqual(baseline, PromptFingerprint.Compute("system", [Alpha with { Description = "Changed." }, Beta]));
        Assert.NotEqual(baseline, PromptFingerprint.Compute("system", [Alpha with { InputSchemaJson = "{}" }, Beta]));
        Assert.NotEqual(PromptFingerprint.Compute("system", []), PromptFingerprint.Compute("system", [Alpha]));
    }

    [Fact]
    public void Part_boundaries_are_unambiguous()
    {
        Assert.NotEqual(
            PromptFingerprint.Compute("s", [new ToolDefinition("ab", "c", "{}")]),
            PromptFingerprint.Compute("s", [new ToolDefinition("a", "bc", "{}")]));
        Assert.NotEqual(
            PromptFingerprint.Compute("sa", [new ToolDefinition("b", "c", "{}")]),
            PromptFingerprint.Compute("s", [new ToolDefinition("ab", "c", "{}")]));
    }

    [Fact]
    public void Long_and_non_ascii_text_is_hashed_whole()
    {
        var prefix = new string('x', 100_000);
        Assert.NotEqual(
            PromptFingerprint.Compute(prefix + "a", []),
            PromptFingerprint.Compute(prefix + "b", []));
        var first = new string([(char)0x89C4, (char)0x5219]);
        var second = new string([(char)0x89C4, (char)0x8303]);
        Assert.NotEqual(PromptFingerprint.Compute(first, []), PromptFingerprint.Compute(second, []));
    }
}
