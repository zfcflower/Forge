using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The compaction budget is only as good as its estimate: a Chinese prompt counted at four characters per token reads as a
/// quarter of its real size, which lets automatic compaction start past the line it was supposed to protect.
/// </summary>
public class TokenCounterTests
{
    private readonly ApproximateTokenCounter counter = new();

    [Fact]
    public void Wide_characters_cost_two_to_three_times_what_latin_text_costs()
    {
        // Four Latin characters per token, unchanged: 160 characters are 40 tokens.
        Assert.Equal(40, counter.Count(new string('a', 160)));

        // One CJK character is worth about 1.6 characters per token: 160 of them are 100 tokens, not 40.
        Assert.Equal(100, counter.Count(new string('汉', 160)));
        Assert.Equal(100, counter.Count(new string('あ', 160)));
        Assert.Equal(100, counter.Count(new string('한', 160)));

        // Mixed text adds up per character: three Latin (6 units) plus one wide (5 units) rounds up to two tokens.
        Assert.Equal(2, counter.Count("abc汉"));
    }

    [Fact]
    public void A_message_carries_its_text_estimate_plus_the_per_message_overhead()
    {
        var wide = counter.Count([ChatMessage.User(new string('汉', 160))]);
        var latin = counter.Count([ChatMessage.User(new string('a', 160))]);

        // 100 tokens of text + 4 of wrapper, against 40 + 4 for the same character count in Latin script.
        Assert.Equal(104, wide);
        Assert.Equal(44, latin);
    }
}
