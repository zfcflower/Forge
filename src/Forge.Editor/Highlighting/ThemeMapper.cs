// Governed by THIRD_PARTY_NOTICES.md.

using Forge.Editor.TextMate.Internal.Selectors;

namespace Forge.Editor.Highlighting;

// One rule of a theme: a TextMate scope selector and the category it gives the scopes it matches.
public readonly record struct ThemeRule(string ScopeSelector, TokenCategory Category);

// Maps scope stacks to the categories a UI renders, through an ordered table of scope selectors. The first matching rule
// wins, so a table lists specific selectors before general ones. Colours are ids the UI resolves: no UI types here.
public sealed class ThemeMapper
{
    private readonly List<(ScopeMatcher Matcher, TokenCategory Category, string Selector)> _rules = [];
    private readonly int[] _foregrounds = new int[Enum.GetValues<TokenCategory>().Length];

    // A mapper with the built-in table: the common TextMate scopes of the bundled grammars.
    public ThemeMapper()
    {
        foreach (var rule in DefaultRules)
        {
            Add(rule.Category, rule.ScopeSelector);
        }
    }

    public IReadOnlyList<ThemeRule> Rules
    {
        get
        {
            var rules = new ThemeRule[_rules.Count];
            for (var i = 0; i < _rules.Count; i++)
            {
                rules[i] = new ThemeRule(_rules[i].Selector, _rules[i].Category);
            }

            return rules;
        }
    }

    // Appends rules at the end of the table, where the general defaults live.
    public ThemeMapper Add(TokenCategory category, params string[] scopeSelectors)
    {
        ArgumentNullException.ThrowIfNull(scopeSelectors);
        foreach (var selector in scopeSelectors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selector);
            foreach (var matcher in ScopeSelector.CreateMatchers(selector, ScopeSelector.ScopeNameMatcher))
            {
                _rules.Add((matcher.Matcher, category, selector));
            }
        }

        return this;
    }

    // Inserts rules at the start of the table, before every default.
    public ThemeMapper AddFirst(TokenCategory category, params string[] scopeSelectors)
    {
        ArgumentNullException.ThrowIfNull(scopeSelectors);
        var inserted = 0;
        foreach (var selector in scopeSelectors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selector);
            foreach (var matcher in ScopeSelector.CreateMatchers(selector, ScopeSelector.ScopeNameMatcher))
            {
                _rules.Insert(inserted++, (matcher.Matcher, category, selector));
            }
        }

        return this;
    }

    // The category of a scope stack, root first.
    public TokenCategory Map(ReadOnlySpan<string> scopes)
    {
        foreach (var rule in _rules)
        {
            if (rule.Matcher.Matches(scopes))
            {
                return rule.Category;
            }
        }

        return TokenCategory.None;
    }

    public TokenCategory Map(string scopeStack)
    {
        ArgumentNullException.ThrowIfNull(scopeStack);
        return Map(scopeStack.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public int GetForeground(TokenCategory category) => _foregrounds[(int)category];

    public ThemeMapper SetForeground(TokenCategory category, int colorId)
    {
        _foregrounds[(int)category] = colorId;
        return this;
    }

    internal int[] Foregrounds => _foregrounds;

    public static ThemeMapper CreateDefault() => new();

    // Specific scopes first: a rule wins over every rule after it, and the built-in table ends with the general ones.
    private static readonly ThemeRule[] DefaultRules =
    [
        new("invalid.illegal", TokenCategory.Invalid),
        new("invalid", TokenCategory.Invalid),
        new("comment", TokenCategory.Comment),

        new("string.regexp", TokenCategory.Regexp),
        new("string", TokenCategory.String),
        new("constant.character.escape", TokenCategory.Escape),

        new("punctuation.definition.comment", TokenCategory.Comment),
        new("punctuation.section.embedded", TokenCategory.Preprocessor),
        new("punctuation", TokenCategory.Punctuation),

        new("constant.numeric", TokenCategory.Number),
        new("constant.language", TokenCategory.Constant),
        new("constant.other", TokenCategory.Constant),
        new("support.constant", TokenCategory.Constant),
        new("constant", TokenCategory.Constant),

        new("keyword.operator", TokenCategory.Operator),
        new("keyword.control.import", TokenCategory.Keyword),
        new("keyword.control.directive", TokenCategory.Preprocessor),
        new("meta.preprocessor", TokenCategory.Preprocessor),
        new("keyword", TokenCategory.Keyword),
        new("storage.modifier", TokenCategory.Modifier),
        new("storage.type", TokenCategory.Keyword),
        new("storage", TokenCategory.Keyword),

        new("entity.name.function", TokenCategory.Function),
        new("support.function", TokenCategory.Function),
        new("meta.function-call", TokenCategory.Function),
        new("meta.function", TokenCategory.Function),

        new("entity.name.type", TokenCategory.Type),
        new("entity.name.class", TokenCategory.Class),
        new("entity.name.struct", TokenCategory.Struct),
        new("entity.name.interface", TokenCategory.Interface),
        new("entity.name.enum", TokenCategory.Enum),
        new("entity.name.namespace", TokenCategory.Namespace),
        new("entity.other.inherited-class", TokenCategory.Type),
        new("entity.name.type-parameter", TokenCategory.TypeParameter),
        new("support.type", TokenCategory.Type),
        new("support.class", TokenCategory.Class),

        new("entity.name.tag", TokenCategory.Tag),
        new("entity.other.attribute-name", TokenCategory.Attribute),
        new("meta.attribute", TokenCategory.Attribute),

        new("entity.name.label", TokenCategory.Label),
        new("entity.name.function.macro", TokenCategory.Macro),
        new("entity.name", TokenCategory.Function),
        new("entity.other", TokenCategory.Variable),

        new("variable.parameter", TokenCategory.Parameter),
        new("variable.other.property", TokenCategory.Property),
        new("variable.language", TokenCategory.Constant),
        new("variable", TokenCategory.Variable),
        new("support.variable", TokenCategory.Variable),

        new("markup.heading", TokenCategory.Keyword),
        new("markup.underline.link", TokenCategory.String),
        new("markup.raw", TokenCategory.String),
        new("markup.inline", TokenCategory.Constant),
        new("markup.list", TokenCategory.Punctuation),
        new("markup.quote", TokenCategory.Comment),
        new("markup.bold", TokenCategory.Constant),
        new("markup.italic", TokenCategory.Constant),
        new("meta.decorator", TokenCategory.Decorator),
    ];
}
