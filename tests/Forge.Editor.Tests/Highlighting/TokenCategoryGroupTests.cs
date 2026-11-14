using Forge.Editor.Highlighting;

namespace Forge.Editor.Tests.Highlighting;

public class TokenCategoryGroupTests
{
    // The whole table: every category either shares a group with the categories that look like it, or explicitly has none.
    private static readonly (TokenCategory Category, TokenCategoryGroup Group)[] Table =
    [
        (TokenCategory.None, TokenCategoryGroup.None),
        (TokenCategory.Keyword, TokenCategoryGroup.Keyword),
        (TokenCategory.String, TokenCategoryGroup.String),
        (TokenCategory.Comment, TokenCategoryGroup.Comment),
        (TokenCategory.Number, TokenCategoryGroup.Number),
        (TokenCategory.Constant, TokenCategoryGroup.Variable),
        (TokenCategory.Type, TokenCategoryGroup.Type),
        (TokenCategory.Function, TokenCategoryGroup.Function),
        (TokenCategory.Variable, TokenCategoryGroup.Variable),
        (TokenCategory.Property, TokenCategoryGroup.Variable),
        (TokenCategory.Parameter, TokenCategoryGroup.Variable),
        (TokenCategory.Operator, TokenCategoryGroup.None),
        (TokenCategory.Punctuation, TokenCategoryGroup.None),
        (TokenCategory.Preprocessor, TokenCategoryGroup.Preprocessor),
        (TokenCategory.Tag, TokenCategoryGroup.None),
        (TokenCategory.Attribute, TokenCategoryGroup.None),
        (TokenCategory.Escape, TokenCategoryGroup.String),
        (TokenCategory.Regexp, TokenCategoryGroup.String),
        (TokenCategory.Modifier, TokenCategoryGroup.Keyword),
        (TokenCategory.Decorator, TokenCategoryGroup.Preprocessor),
        (TokenCategory.Label, TokenCategoryGroup.None),
        (TokenCategory.Macro, TokenCategoryGroup.Function),
        (TokenCategory.Namespace, TokenCategoryGroup.Type),
        (TokenCategory.Class, TokenCategoryGroup.Type),
        (TokenCategory.Struct, TokenCategoryGroup.Type),
        (TokenCategory.Interface, TokenCategoryGroup.Type),
        (TokenCategory.Enum, TokenCategoryGroup.Type),
        (TokenCategory.EnumMember, TokenCategoryGroup.Type),
        (TokenCategory.Event, TokenCategoryGroup.Variable),
        (TokenCategory.TypeParameter, TokenCategoryGroup.Type),
        (TokenCategory.Meta, TokenCategoryGroup.None),
        (TokenCategory.Invalid, TokenCategoryGroup.Invalid),
    ];

    public static TheoryData<TokenCategory, TokenCategoryGroup> Mappings()
    {
        var mappings = new TheoryData<TokenCategory, TokenCategoryGroup>();
        foreach (var (category, group) in Table)
        {
            mappings.Add(category, group);
        }

        return mappings;
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void A_category_resolves_to_the_group_that_paints_it(TokenCategory category, TokenCategoryGroup expected)
    {
        Assert.Equal(expected, TokenCategoryGroups.Group(category));
    }

    [Fact]
    public void The_table_covers_every_category_the_editor_can_produce()
    {
        // A new TokenCategory that nobody grouped would silently paint as None; this fails instead.
        Assert.Equal(
            Enum.GetValues<TokenCategory>().OrderBy(category => (int)category).ToArray(),
            Table.Select(row => row.Category).OrderBy(category => (int)category).ToArray());
    }

    [Fact]
    public void An_undefined_category_has_no_group()
    {
        Assert.Equal(TokenCategoryGroup.None, TokenCategoryGroups.Group((TokenCategory)200));
    }
}
