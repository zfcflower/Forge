// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// The LSP semantic token types (the standard set plus the common modifiers) mapped onto the categories the lexical layer
// uses, so a merged view renders both layers the same way.
public static class SemanticTokenMapper
{
    private static readonly Dictionary<string, TokenCategory> s_categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["namespace"] = TokenCategory.Namespace,
        ["type"] = TokenCategory.Type,
        ["class"] = TokenCategory.Class,
        ["enum"] = TokenCategory.Enum,
        ["interface"] = TokenCategory.Interface,
        ["struct"] = TokenCategory.Struct,
        ["typeParameter"] = TokenCategory.TypeParameter,
        ["parameter"] = TokenCategory.Parameter,
        ["variable"] = TokenCategory.Variable,
        ["property"] = TokenCategory.Property,
        ["enumMember"] = TokenCategory.EnumMember,
        ["event"] = TokenCategory.Event,
        ["function"] = TokenCategory.Function,
        ["method"] = TokenCategory.Function,
        ["macro"] = TokenCategory.Macro,
        ["keyword"] = TokenCategory.Keyword,
        ["modifier"] = TokenCategory.Modifier,
        ["comment"] = TokenCategory.Comment,
        ["string"] = TokenCategory.String,
        ["number"] = TokenCategory.Number,
        ["regexp"] = TokenCategory.Regexp,
        ["operator"] = TokenCategory.Operator,
        ["decorator"] = TokenCategory.Decorator,
        ["label"] = TokenCategory.Label,
    };

    public static IReadOnlyCollection<string> KnownTokenTypes => s_categories.Keys;

    public static bool TryGetCategory(string lspTokenType, out TokenCategory category)
    {
        ArgumentNullException.ThrowIfNull(lspTokenType);
        if (s_categories.TryGetValue(lspTokenType, out category))
        {
            return true;
        }

        category = TokenCategory.None;
        return false;
    }

    public static TokenCategory GetCategory(string lspTokenType) => TryGetCategory(lspTokenType, out var category) ? category : TokenCategory.None;

    // The token types of a server's legend mapped once, so a decoded delta only indexes an array.
    public static TokenCategory[] MapLegend(IEnumerable<string> tokenTypes)
    {
        ArgumentNullException.ThrowIfNull(tokenTypes);
        return [.. tokenTypes.Select(GetCategory)];
    }
}
