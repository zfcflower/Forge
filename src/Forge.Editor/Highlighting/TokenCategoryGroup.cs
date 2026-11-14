// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.Highlighting;

// The coarse group a UI paints a token with: several lexical categories share one colour, and there are fewer groups than
// categories, so both the editor's text runs and the diff viewer's syntax spans resolve a group instead of a category.
public enum TokenCategoryGroup : byte
{
    /// <summary>No colour of its own: the text keeps whatever its surroundings paint it with.</summary>
    None = 0,
    Keyword,
    String,
    Comment,
    Number,
    Type,
    Function,
    Variable,
    Preprocessor,
    Invalid,
}

public static class TokenCategoryGroups
{
    // The one table that decides which categories look alike. Ordered like TokenCategory so it reads as a grouping of the
    // enum, not as a priority list.
    public static TokenCategoryGroup Group(TokenCategory category) => category switch
    {
        TokenCategory.Keyword or TokenCategory.Modifier => TokenCategoryGroup.Keyword,
        TokenCategory.String or TokenCategory.Escape or TokenCategory.Regexp => TokenCategoryGroup.String,
        TokenCategory.Comment => TokenCategoryGroup.Comment,
        TokenCategory.Number => TokenCategoryGroup.Number,
        TokenCategory.Type or TokenCategory.Class or TokenCategory.Struct or TokenCategory.Interface or TokenCategory.Enum
            or TokenCategory.EnumMember or TokenCategory.Namespace or TokenCategory.TypeParameter => TokenCategoryGroup.Type,
        TokenCategory.Function or TokenCategory.Macro => TokenCategoryGroup.Function,
        TokenCategory.Constant or TokenCategory.Variable or TokenCategory.Property or TokenCategory.Parameter
            or TokenCategory.Event => TokenCategoryGroup.Variable,
        TokenCategory.Preprocessor or TokenCategory.Decorator => TokenCategoryGroup.Preprocessor,
        TokenCategory.Invalid => TokenCategoryGroup.Invalid,
        _ => TokenCategoryGroup.None,
    };
}
