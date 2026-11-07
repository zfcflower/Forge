using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Forge.App.ViewModels;
using Forge.Editor.Highlighting;

namespace Forge.App.Workbench.Controls;

internal sealed class SyntaxLineText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<FileTextSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SyntaxLineText, IReadOnlyList<FileTextSegment>?>(nameof(Segments));

    static SyntaxLineText()
    {
        SegmentsProperty.Changed.AddClassHandler<SyntaxLineText>((control, _) => control.Rebuild());
    }

    public IReadOnlyList<FileTextSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        if (Segments is not { } segments)
        {
            return;
        }

        foreach (var segment in segments)
        {
            IBrush? brush = null;
            if (BrushKey(segment.Category) is { } key && this.TryFindResource(key, ActualThemeVariant, out var found))
            {
                brush = found as IBrush;
            }

            Inlines.Add(new Run(segment.Text) { Foreground = brush });
        }
    }

    private static string? BrushKey(TokenCategory category) => category switch
    {
        TokenCategory.Keyword or TokenCategory.Modifier => "SyntaxKeywordBrush",
        TokenCategory.String or TokenCategory.Escape or TokenCategory.Regexp => "SyntaxStringBrush",
        TokenCategory.Comment => "SyntaxCommentBrush",
        TokenCategory.Number => "SyntaxNumberBrush",
        TokenCategory.Type or TokenCategory.Class or TokenCategory.Struct or TokenCategory.Interface or TokenCategory.Enum
            or TokenCategory.EnumMember or TokenCategory.Namespace or TokenCategory.TypeParameter => "SyntaxTypeBrush",
        TokenCategory.Function or TokenCategory.Macro => "SyntaxFunctionBrush",
        TokenCategory.Constant or TokenCategory.Variable or TokenCategory.Property or TokenCategory.Parameter
            or TokenCategory.Event => "SyntaxVariableBrush",
        TokenCategory.Preprocessor or TokenCategory.Decorator => "SyntaxPreprocessorBrush",
        TokenCategory.Invalid => "DangerBrush",
        _ => null,
    };
}
