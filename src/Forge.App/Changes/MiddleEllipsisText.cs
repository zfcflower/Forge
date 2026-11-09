using Avalonia.Controls.Documents;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Changes;

/// <summary>
/// One line of text that shortens itself in the middle ("src/Forge.App/Lo…Strings.Inspector.cs") when it does not fit, so a
/// long path keeps both its root and its file name. Measuring runs on width/text changes only; the formatted text is cached.
/// </summary>
internal sealed class MiddleEllipsisText : Control
{
    private const string Ellipsis = "…";

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MiddleEllipsisText, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<MiddleEllipsisText>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<MiddleEllipsisText>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MiddleEllipsisText>();

    private FormattedText? cached;
    private string? cachedFor;
    private double cachedWidth = -1;
    private string shown = string.Empty;

    static MiddleEllipsisText()
    {
        AffectsMeasure<MiddleEllipsisText>(TextProperty, FontFamilyProperty, FontSizeProperty);
        AffectsRender<MiddleEllipsisText>(ForegroundProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The text actually drawn (with the ellipsis when shortened); for tests.</summary>
    internal string DisplayedText => shown;

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? string.Empty;
        var width = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        Fit(text, width);
        return new Size(Math.Min(cached!.Width, double.IsInfinity(availableSize.Width) ? cached.Width : availableSize.Width), cached.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
            || change.Property == TextProperty)
        {
            cachedFor = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Fit(Text ?? string.Empty, Bounds.Width);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.DrawText(cached!, default);
    }

    private void Fit(string text, double width)
    {
        if (cached is not null && cachedFor == text && Math.Abs(cachedWidth - width) < 0.5)
        {
            return;
        }

        var full = Format(text);
        if (full.Width <= width || text.Length <= 2)
        {
            cached = full;
            shown = text;
        }
        else
        {
            // Largest number of kept characters whose shortened form fits; a third more of the tail than the head, because the file
            // name is what tells paths apart.
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Format(Shorten(text, mid)).Width <= width)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            shown = Shorten(text, lo);
            cached = Format(shown);
        }

        cachedFor = text;
        cachedWidth = width;
    }

    /// <summary>The text with its middle replaced by an ellipsis so that <paramref name="keep"/> characters of it remain.</summary>
    internal static string Shorten(string text, int keep)
    {
        keep = Math.Clamp(keep, 0, text.Length);
        if (keep >= text.Length)
        {
            return text;
        }

        var head = keep * 2 / 5;
        var tail = keep - head;
        return string.Concat(text.AsSpan(0, head), Ellipsis, text.AsSpan(text.Length - tail));
    }

    private FormattedText Format(string text) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Foreground ?? Brushes.Black);
}
