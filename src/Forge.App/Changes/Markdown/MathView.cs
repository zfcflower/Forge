using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// One TeX formula, drawn by <see cref="MathBuilder"/>. Inline formulas sit on the text baseline (the control reports its
/// descent as a negative bottom margin, so the text line aligns with the formula's baseline); a display formula is centred in
/// its own row. A formula outside the supported TeX subset shows its source in the code font instead. The layout is rebuilt only
/// when the text size, the colour or the size of the formula changes - drawing is just replaying the boxes.
/// </summary>
internal sealed class MathView : Control
{
    private static readonly Typeface Face = new("Cambria Math, STIX Two Math, Latin Modern Math, Segoe UI Symbol, Times New Roman");

    private readonly string tex;
    private readonly bool display;
    private readonly MathNode? node;
    private MathBox? box;
    private FormattedText? source;
    private double builtSize;
    private IBrush? builtBrush;

    internal MathView(string tex, bool display)
    {
        this.tex = tex.Trim();
        this.display = display;
        node = this.tex.Length == 0 ? null : MathParser.TryParse(this.tex);
        if (display)
        {
            HorizontalAlignment = HorizontalAlignment.Center;
            Margin = new Thickness(0, 4);
        }

        ToolTip.SetTip(this, this.tex);
    }

    private double TextSize => this.GetValue(TextElement.FontSizeProperty) is var size and > 0 ? size : 14;

    private IBrush? TextBrush => this.GetValue(TextElement.ForegroundProperty);

    private void Build()
    {
        var size = display ? TextSize * 1.15 : TextSize;
        var brush = TextBrush;
        if (builtSize == size && ReferenceEquals(builtBrush, brush) && (box is not null || source is not null))
        {
            return;
        }

        builtSize = size;
        builtBrush = brush;
        box = null;
        source = null;
        if (node is not null)
        {
            box = new MathBuilder(Face, brush).Build(node, size, display);
            return;
        }

        var mono = this.TryFindResource("FontMono", ActualThemeVariant, out var font) && font is FontFamily family ? family : FontFamily.Default;
        source = new FormattedText(tex, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(mono), size * 0.9, brush);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextElement.FontSizeProperty || change.Property == TextElement.ForegroundProperty)
        {
            builtSize = 0;
            AlignBaseline();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AlignBaseline();
    }

    /// <summary>Inline: pull the bottom edge up to the baseline (the descent is drawn into the line's descender space).</summary>
    private void AlignBaseline()
    {
        if (display)
        {
            return;
        }

        Build();
        var margin = new Thickness(0, 0, 0, -Math.Min(box?.Descent ?? 0, 6));
        if (Margin != margin)
        {
            Margin = margin;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Build();
        if (box is { } laidOut)
        {
            return new Size(laidOut.Width + 2, laidOut.Ascent + laidOut.Descent + 2);
        }

        return source is { } text ? new Size(text.WidthIncludingTrailingWhitespace, text.Height) : default;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Build();
        if (box is { } laidOut)
        {
            laidOut.Draw(context, 1, laidOut.Ascent + 1);
        }
        else if (source is { } text)
        {
            context.DrawText(text, default);
        }
    }

    /// <summary>The descent of the laid-out formula, which an inline host uses to align the baseline.</summary>
    internal double Descent
    {
        get
        {
            Build();
            return box?.Descent ?? 0;
        }
    }

    /// <summary>The inline container a text block holds for an inline formula, baseline-aligned.</summary>
    internal static InlineUIContainer Inline(string tex)
    {
        var view = new MathView(tex, display: false);
        view.AlignBaseline();
        return new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Baseline };
    }
}
