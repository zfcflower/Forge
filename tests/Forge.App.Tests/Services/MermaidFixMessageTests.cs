using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Views;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the prompt a failed Mermaid block hands back to the model (wave W11): the error and the source travel together so the
/// model can fix the diagram in one turn, the fence outgrows any run of backticks in the source (a diagram whose labels
/// contain one must not end the block early), an oversized source is cut with a note instead of flooding the prompt, and both
/// UI languages are formatted.
/// </summary>
public sealed class MermaidFixMessageTests
{
    private const string Flow = "flowchart TD\n  A[Start] --> B{Done}\n";
    private const string Error = "Invalid mermaid header: \"whatever\"";

    [Fact]
    public void The_message_carries_the_error_and_the_fenced_source()
    {
        using var english = new LanguageScope(UiLanguage.English);

        var message = MermaidFixMessage.Build(Flow, Error);

        Assert.Contains(Error, message, StringComparison.Ordinal);
        Assert.Contains(Flow, message, StringComparison.Ordinal);
        Assert.Contains("```mermaid\n" + Flow + "\n```", message, StringComparison.Ordinal);
        Assert.Contains("fix", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("flowchart TD\n  A-->B\n", "```")]
    [InlineData("flowchart TD\n  A[```]-->B\n", "````")]
    [InlineData("flowchart TD\n  A[````]-->B\n", "`````")]
    [InlineData("no ticks at all", "```")]
    public void The_fence_grows_past_the_longest_backtick_run_in_the_source(string source, string expected) =>
        Assert.Equal(expected, MermaidFixMessage.Fence(source));

    [Fact]
    public void A_source_over_the_limit_is_truncated_with_a_note()
    {
        using var english = new LanguageScope(UiLanguage.English);
        var source = new string('x', MermaidFixMessage.MaxSourceCharacters) + "TAIL-IS-CUT";

        var message = MermaidFixMessage.Build(source, Error);

        Assert.DoesNotContain("TAIL-IS-CUT", message, StringComparison.Ordinal);
        Assert.Contains(new string('x', MermaidFixMessage.MaxSourceCharacters), message, StringComparison.Ordinal);
        Assert.Contains("the source was truncated", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_languages_format_the_message()
    {
        var previous = Loc.Instance.Language;
        try
        {
            Loc.Instance.Language = UiLanguage.English;
            var english = MermaidFixMessage.Build(Flow, Error);

            Loc.Instance.Language = UiLanguage.Chinese;
            var chinese = MermaidFixMessage.Build(Flow, Error);

            Assert.Contains("This Mermaid diagram failed to render locally", english, StringComparison.Ordinal);
            Assert.Contains("这张 Mermaid 图在本地渲染失败", chinese, StringComparison.Ordinal);
            Assert.NotEqual(english, chinese);
            // The error, the source and the fence are the same facts in both languages.
            Assert.Contains(Error, chinese, StringComparison.Ordinal);
            Assert.Contains("```mermaid\n" + Flow + "\n```", chinese, StringComparison.Ordinal);
        }
        finally
        {
            Loc.Instance.Language = previous;
        }
    }
}
