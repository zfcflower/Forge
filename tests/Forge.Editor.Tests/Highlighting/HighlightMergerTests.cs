using Forge.Editor.Highlighting;
using Forge.Editor.TextMate;

namespace Forge.Editor.Tests.Highlighting;

public class HighlightMergerTests
{
    [Fact]
    public void Semantic_token_wins_on_its_range_and_lexical_keeps_the_rest()
    {
        HighlightToken[] lexical = [new(0, 5, 7), new(5, 5, 8)];
        TokenCategory[] categories = [TokenCategory.Keyword, TokenCategory.None];
        SemanticToken[] semantic = [new(2, 2, TokenCategory.Variable)];

        var merged = HighlightMerger.Overlay(lexical, categories, semantic);

        Assert.Equal(
            new[]
            {
                new StyledToken(0, 2, TokenCategory.Keyword, 7),
                new StyledToken(2, 2, TokenCategory.Variable, -1),
                new StyledToken(4, 1, TokenCategory.Keyword, 7),
                new StyledToken(5, 5, TokenCategory.None, 8),
            },
            merged);
    }

    [Fact]
    public void Semantic_token_spanning_two_lexical_tokens_splits_both()
    {
        HighlightToken[] lexical = [new(0, 4, 1), new(4, 4, 2)];
        TokenCategory[] categories = [TokenCategory.Keyword, TokenCategory.String];
        SemanticToken[] semantic = [new(2, 4, TokenCategory.Function)];

        var merged = HighlightMerger.Overlay(lexical, categories, semantic);

        Assert.Equal(
            new[]
            {
                new StyledToken(0, 2, TokenCategory.Keyword, 1),
                new StyledToken(2, 4, TokenCategory.Function, -1),
                new StyledToken(6, 2, TokenCategory.String, 2),
            },
            merged);
    }

    [Fact]
    public void Lexical_only_and_semantic_only_inputs_are_copied_through()
    {
        HighlightToken[] lexical = [new(1, 2, 9)];
        TokenCategory[] categories = [TokenCategory.Comment];
        SemanticToken[] semantic = [new(0, 1, TokenCategory.Class)];

        Assert.Equal(new[] { new StyledToken(1, 2, TokenCategory.Comment, 9) }, HighlightMerger.Overlay(lexical, categories, []));
        Assert.Equal(new[] { new StyledToken(0, 1, TokenCategory.Class, -1) }, HighlightMerger.Overlay([], [], semantic));
    }

    [Fact]
    public void Gaps_between_inputs_are_not_emitted()
    {
        HighlightToken[] lexical = [new(0, 3, 1)];
        TokenCategory[] categories = [TokenCategory.Keyword];
        SemanticToken[] semantic = [new(5, 2, TokenCategory.Variable)];

        var merged = HighlightMerger.Overlay(lexical, categories, semantic);

        Assert.Equal(
            new[]
            {
                new StyledToken(0, 3, TokenCategory.Keyword, 1),
                new StyledToken(5, 2, TokenCategory.Variable, -1),
            },
            merged);
    }

    [Fact]
    public void Empty_inputs_produce_no_tokens_and_mismatched_categories_are_rejected()
    {
        Assert.Empty(HighlightMerger.Overlay([], [], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => HighlightMerger.Overlay([new(0, 1, 1)], [], []));
    }

    [Fact]
    public void Semantic_token_covering_the_whole_line_replaces_every_lexical_token()
    {
        HighlightToken[] lexical = [new(0, 2, 1), new(2, 2, 2), new(4, 2, 3)];
        TokenCategory[] categories = [TokenCategory.Keyword, TokenCategory.String, TokenCategory.Number];
        SemanticToken[] semantic = [new(0, 6, TokenCategory.Macro)];

        var merged = HighlightMerger.Overlay(lexical, categories, semantic);

        Assert.Equal(new[] { new StyledToken(0, 6, TokenCategory.Macro, -1) }, merged);
    }

    [Fact]
    public void Rendered_line_overlays_semantic_tokens_from_the_caller()
    {
        var tokenizer = new Tokenizer(new GrammarRegistry());
        var result = tokenizer.HighlightText("int count;", "csharp");
        SemanticToken[] semantic = [new(4, 5, TokenCategory.Variable)];

        var rendered = result.RenderLine(0, semantic);

        var variable = Assert.Single(rendered, token => token.Category == TokenCategory.Variable);
        Assert.Equal((4, 9, -1), (variable.Start, variable.End, variable.ScopeId));
        Assert.Equal(TokenCategory.Keyword, rendered[0].Category);
    }

    [Fact]
    public void Default_theme_maps_common_scopes_to_categories()
    {
        var theme = ThemeMapper.CreateDefault();

        Assert.Equal(TokenCategory.Comment, theme.Map("source.cs comment.line.double-slash.cs"));
        Assert.Equal(TokenCategory.Keyword, theme.Map("source.cs keyword.control.cs"));
        Assert.Equal(TokenCategory.Operator, theme.Map("source.cs keyword.operator.assignment.cs"));
        Assert.Equal(TokenCategory.Regexp, theme.Map("string.regexp"));
        Assert.Equal(TokenCategory.Number, theme.Map("constant.numeric.decimal.cs"));
        Assert.Equal(TokenCategory.Function, theme.Map("entity.name.function.cs"));
        Assert.Equal(TokenCategory.Type, theme.Map("entity.name.type.cs"));
        Assert.Equal(TokenCategory.Type, theme.Map("entity.name.type.class.cs"));
        Assert.Equal(TokenCategory.Variable, theme.Map("variable.other.readwrite.cs"));
        Assert.Equal(TokenCategory.Invalid, theme.Map("invalid.illegal.bad"));
        Assert.Equal(TokenCategory.None, theme.Map("source.cs foo.bar"));
        Assert.Equal(TokenCategory.None, theme.Map(string.Empty));
    }

    [Fact]
    public void Specific_rules_win_over_general_ones_and_order_is_first_match()
    {
        var theme = ThemeMapper.CreateDefault();

        Assert.Equal(TokenCategory.Regexp, theme.Map("string.regexp.js"));
        Assert.Equal(TokenCategory.String, theme.Map("string.quoted.double"));
        Assert.Equal(TokenCategory.Preprocessor, theme.Map("meta.preprocessor.cs"));
        Assert.Equal(TokenCategory.Keyword, theme.Map("keyword.control.import.cs"));
    }

    [Fact]
    public void Span_and_string_forms_of_map_agree()
    {
        var theme = ThemeMapper.CreateDefault();
        string[] scopes = ["source.cs", "string.quoted.double.cs"];

        Assert.Equal(theme.Map(scopes), theme.Map("source.cs string.quoted.double.cs"));
    }

    [Fact]
    public void Added_rules_go_after_defaults_while_first_rules_go_before_them()
    {
        var theme = new ThemeMapper()
            .Add(TokenCategory.Macro, "keyword.custom")
            .AddFirst(TokenCategory.Label, "comment");

        Assert.Equal(TokenCategory.Label, theme.Map("comment.line"));
        Assert.Equal(TokenCategory.Keyword, theme.Map("keyword.custom.thing"));
        var last = theme.Rules[^1];
        Assert.Equal(new ThemeRule("keyword.custom", TokenCategory.Macro), last);
        Assert.Equal(new ThemeRule("comment", TokenCategory.Label), theme.Rules[0]);
    }

    [Fact]
    public void Custom_selector_matches_scopes_with_extra_suffixes()
    {
        var theme = new ThemeMapper().Add(TokenCategory.Tag, "entity.name.tag.html");

        Assert.Equal(TokenCategory.Tag, theme.Map("text.html entity.name.tag.html.custom"));
    }

    [Fact]
    public void Invalid_rule_arguments_are_rejected()
    {
        var theme = new ThemeMapper();

        Assert.Throws<ArgumentException>(() => theme.Add(TokenCategory.Keyword, " "));
        Assert.Throws<ArgumentNullException>(() => theme.Add(TokenCategory.Keyword, null!));
        Assert.Throws<ArgumentException>(() => theme.AddFirst(TokenCategory.Keyword, ""));
        Assert.Throws<ArgumentNullException>(() => theme.Map((string)null!));
    }

    [Fact]
    public void Foreground_colours_default_to_zero_and_store_per_category()
    {
        var theme = new ThemeMapper();

        Assert.Equal(0, theme.GetForeground(TokenCategory.Keyword));
        var returned = theme.SetForeground(TokenCategory.Keyword, 12);

        Assert.Same(theme, returned);
        Assert.Equal(12, theme.GetForeground(TokenCategory.Keyword));
        Assert.Equal(0, theme.GetForeground(TokenCategory.String));
    }
}
