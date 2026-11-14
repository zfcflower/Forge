using Avalonia;
using Avalonia.Controls;
using Forge.Editor.Highlighting;

namespace Forge.App.Workbench.Controls;

public sealed class SyntaxTokenTextBlock : TextBlock
{
    public static readonly StyledProperty<TokenCategory> CategoryProperty =
        AvaloniaProperty.Register<SyntaxTokenTextBlock, TokenCategory>(nameof(Category));

    static SyntaxTokenTextBlock()
    {
        CategoryProperty.Changed.AddClassHandler<SyntaxTokenTextBlock>((control, _) => control.UpdateCategoryClasses());
    }

    public TokenCategory Category
    {
        get => GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    public SyntaxTokenTextBlock()
    {
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
    }

    private void UpdateCategoryClasses()
    {
        // Which categories look alike is decided once, in the editor layer, because the editor's semantic overlay paints
        // from the same table; this control only names the classes.
        var group = TokenCategoryGroups.Group(Category);
        Classes.Set("syntax-keyword", group == TokenCategoryGroup.Keyword);
        Classes.Set("syntax-string", group == TokenCategoryGroup.String);
        Classes.Set("syntax-comment", group == TokenCategoryGroup.Comment);
        Classes.Set("syntax-number", group == TokenCategoryGroup.Number);
        Classes.Set("syntax-type", group == TokenCategoryGroup.Type);
        Classes.Set("syntax-function", group == TokenCategoryGroup.Function);
        Classes.Set("syntax-variable", group == TokenCategoryGroup.Variable);
        Classes.Set("syntax-preprocessor", group == TokenCategoryGroup.Preprocessor);
        Classes.Set("syntax-invalid", group == TokenCategoryGroup.Invalid);
    }
}
