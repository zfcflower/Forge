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
        var category = Category;
        Classes.Set("syntax-keyword", category is TokenCategory.Keyword or TokenCategory.Modifier);
        Classes.Set("syntax-string", category is TokenCategory.String or TokenCategory.Escape or TokenCategory.Regexp);
        Classes.Set("syntax-comment", category == TokenCategory.Comment);
        Classes.Set("syntax-number", category == TokenCategory.Number);
        Classes.Set(
            "syntax-type",
            category is TokenCategory.Type
                or TokenCategory.Class
                or TokenCategory.Struct
                or TokenCategory.Interface
                or TokenCategory.Enum
                or TokenCategory.EnumMember
                or TokenCategory.Namespace
                or TokenCategory.TypeParameter);
        Classes.Set("syntax-function", category is TokenCategory.Function or TokenCategory.Macro);
        Classes.Set(
            "syntax-variable",
            category is TokenCategory.Constant
                or TokenCategory.Variable
                or TokenCategory.Property
                or TokenCategory.Parameter
                or TokenCategory.Event);
        Classes.Set("syntax-preprocessor", category is TokenCategory.Preprocessor or TokenCategory.Decorator);
        Classes.Set("syntax-invalid", category == TokenCategory.Invalid);
    }
}
