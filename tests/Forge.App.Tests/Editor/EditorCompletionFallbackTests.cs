using AvaloniaEdit.Document;
using Forge.App.Editing;
using Xunit;

namespace Forge.App.Tests.Editor;

/// <summary>
/// Pins the two completion fallbacks: an LSP snippet becomes the plain text it would have inserted, and a file without a
/// language server still completes from its own words.
/// </summary>
public class EditorCompletionFallbackTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("for (var ${1:i} = 0; ${1:i} < ${2:n}; ${1:i}++)", "for (var i = 0; i < n; i++)")]
    [InlineData("$0", "")]
    [InlineData("${1}", "")]
    [InlineData("${1|one,two,three|}", "one")]
    [InlineData("\\$notAPlaceholder", "$notAPlaceholder")]
    [InlineData("$TM_FILENAME $CURRENT_YEAR", " ")]
    [InlineData("a$", "a$")]
    [InlineData("${1:text with \\} brace}", "text with } brace")]
    [InlineData("${1:unterminated", "")]
    [InlineData("", "")]
    public void A_snippet_becomes_the_text_it_would_insert(string snippet, string expected) =>
        Assert.Equal(expected, EditorLspSnippets.ToPlainText(snippet));

    [Fact]
    public void A_nested_placeholder_is_resolved_inside_its_default_text() =>
        Assert.Equal("outer inner", EditorLspSnippets.ToPlainText("${1:outer ${2:inner}}"));

    [Fact]
    public void Word_completion_offers_the_words_that_start_with_the_typed_prefix()
    {
        var document = new TextDocument("beta = alpha + betatron;\nalpha = beta;\nbetal(alpha);\nbet");

        var completion = EditorWordCompletion.Build(document, document.TextLength);

        Assert.NotNull(completion);
        Assert.Equal(document.TextLength - 3, completion!.PrefixStart);
        Assert.Equal(["beta", "betal", "betatron"], completion.Items.Select(static item => item.Label));
        Assert.All(completion.Items, item => Assert.Equal(new EditorLspEdit(document.TextLength - 3, document.TextLength, item.Label), item.Edit));
        Assert.False(completion.IsIncomplete);
    }

    [Fact]
    public void Word_completion_is_empty_without_a_prefix_or_without_words()
    {
        var document = new TextDocument("alpha beta");

        Assert.Null(EditorWordCompletion.Build(document, 0));
        Assert.Null(EditorWordCompletion.Build(new TextDocument(" "), 1));
        Assert.Null(EditorWordCompletion.Build(new TextDocument("zz"), 2));
    }

    [Fact]
    public void Word_completion_never_offers_the_prefix_itself()
    {
        var document = new TextDocument("value value value");

        Assert.Null(EditorWordCompletion.Build(document, document.TextLength));
    }

    [Fact]
    public void Word_completion_ranks_by_frequency_and_caps_the_list()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            builder.Append("word").Append(i).Append(' ').Append("word").Append(i).Append(' ');
        }

        var document = new TextDocument(builder.ToString() + "word");
        var completion = EditorWordCompletion.Build(document, document.TextLength);

        Assert.NotNull(completion);
        Assert.Equal(EditorWordCompletion.MaxItems, completion!.Items.Count);
        Assert.Equal("word0", completion.Items[0].Label);
        Assert.Equal("word1", completion.Items[1].Label);
    }

    [Fact]
    public void Word_completion_scans_only_a_window_of_a_large_document()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("farawayword ");
        builder.Append('x', EditorWordCompletion.MaxScanChars);
        builder.Append(" nearbyword nea");

        var document = new TextDocument(builder.ToString());
        var completion = EditorWordCompletion.Build(document, document.TextLength);

        Assert.NotNull(completion);
        Assert.Equal(["nearbyword"], completion!.Items.Select(static item => item.Label));
    }

    [Fact]
    public void A_word_is_a_run_of_letters_digits_and_underscores()
    {
        Assert.True(EditorWordCompletion.IsWordCharacter('a'));
        Assert.True(EditorWordCompletion.IsWordCharacter('7'));
        Assert.True(EditorWordCompletion.IsWordCharacter('_'));
        Assert.False(EditorWordCompletion.IsWordCharacter(' '));
        Assert.False(EditorWordCompletion.IsWordCharacter('-'));
    }
}
