using System.Globalization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the prompt a failed Mermaid block hands back to the model (wave W11): the error and the source travel together so the
/// model can fix the diagram in one turn, the fence outgrows any run of backticks in the source (a diagram whose labels
/// contain one must not end the block early), an oversized source is cut with a note instead of flooding the prompt, and the
/// whole message is fixed English: it is an instruction to another AI, not interface copy, so it does not follow the UI
/// language (the button, the status and the origin label the user sees are the localized half).
/// </summary>
public sealed class MermaidFixMessageTests
{
    private const string Flow = "flowchart TD\n  A[Start] --> B{Done}\n";
    private const string Error = "Invalid mermaid header: \"whatever\"";

    /// <summary>The hand-off verbatim, as the model must receive it whatever the interface language is.</summary>
    private const string Request =
        "This Mermaid diagram failed to render locally; please fix it.\n\nError: {0}\n\nSource:\n{2}mermaid\n{1}\n{2}\n\nReply with the corrected, complete Mermaid code block only.";

    private const string Truncated = "… (the source was truncated)";

    [Fact]
    public void The_message_carries_the_error_and_the_fenced_source_in_fixed_English()
    {
        var message = MermaidFixMessage.Build(Flow, Error);

        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Request, Error, Flow, "```"), message);
        Assert.Contains(Error, message, StringComparison.Ordinal);
        Assert.Contains(Flow, message, StringComparison.Ordinal);
        // The fence is exactly three backticks (the source has none), on its own lines, with nothing glued to either end.
        Assert.Contains("Source:\n```mermaid\n" + Flow + "\n```\n\nReply", message, StringComparison.Ordinal);
        Assert.DoesNotContain("````", message, StringComparison.Ordinal);
        Assert.Contains("fix", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_message_is_the_same_English_in_every_UI_language(bool chinese)
    {
        using var scope = new LanguageScope(chinese ? UiLanguage.Chinese : UiLanguage.English);

        // The message is model-directed, so the interface language cannot change a word of it.
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, Request, Error, Flow, "```"), MermaidFixMessage.Build(Flow, Error));
    }

    [Fact]
    public void The_message_holds_no_CJK_character()
    {
        var message = MermaidFixMessage.Build(Flow, Error);

        // Model-directed text is fixed English: no ideograph, CJK punctuation mark or fullwidth character may appear.
        Assert.All(message, static c => Assert.False(IsCjk(c), $"the hand-off message holds the CJK character '{c}'"));
    }

    [Fact]
    public void A_source_that_contains_a_fence_is_wrapped_in_one_backtick_more_than_its_longest_run()
    {
        var source = "flowchart TD\n  A[````]-->B\n";

        var message = MermaidFixMessage.Build(source, Error);

        // The longest run is 4, so the fence is 5 - not the 8 that a template repeating the fence's own backticks would give.
        Assert.Contains("\n`````mermaid\n" + source + "\n`````\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("``````", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowchart TD\n  A-->B\n", "```")]
    [InlineData("flowchart TD\n  A[```]-->B\n", "````")]
    [InlineData("flowchart TD\n  A[````]-->B\n", "`````")]
    [InlineData("no ticks at all", "```")]
    public void The_fence_grows_past_the_longest_backtick_run_in_the_source(string source, string expected) =>
        Assert.Equal(expected, MermaidFixMessage.Fence(source));

    [Fact]
    public void A_source_over_the_limit_is_truncated_with_the_fixed_English_note()
    {
        var source = new string('x', MermaidFixMessage.MaxSourceCharacters) + "TAIL-IS-CUT";

        var message = MermaidFixMessage.Build(source, Error);

        Assert.DoesNotContain("TAIL-IS-CUT", message, StringComparison.Ordinal);
        Assert.Contains(new string('x', MermaidFixMessage.MaxSourceCharacters), message, StringComparison.Ordinal);
        Assert.Contains(Truncated, message, StringComparison.Ordinal);
    }

    private static bool IsCjk(char c) =>
        c is >= '\u4E00' and <= '\u9FFF' or >= '\u3000' and <= '\u303F' or >= '\uFF00' and <= '\uFFEF';
}
