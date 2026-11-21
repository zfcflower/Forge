using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Forge.App.Views;

/// <summary>
/// Gives every trimmed <see cref="TextBlock"/> a tooltip with its full text: a text that fits shows none, one cut to an
/// ellipsis by a narrow column shows the whole of it on hover. Only tooltips this helper set are touched, so a text with
/// a tooltip of its own keeps it.
/// <para>The tip's content is a <see cref="TextBlock"/> this helper owns (<see cref="ToolTipText"/>), not a plain string:
/// Avalonia hands a tooltip that hangs on a <see cref="TextBlock"/> its owner's text layout, so the trimmed one-line owner
/// forced <c>MaxLines=1</c> and an ellipsis onto the tip itself and the reader only ever saw the beginning. The opening
/// handler puts those two back — a string tip's presenter builds its own text, which nothing here can reach.</para>
/// </summary>
internal static class TrimmedTextToolTip
{
    private static readonly AttachedProperty<bool> OwnsTipProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("OwnsTrimmedTip", typeof(TrimmedTextToolTip));

    /// <summary>The tip content this helper parked on a text, so the next change rewrites it instead of building another.</summary>
    private static readonly AttachedProperty<TextBlock?> TipContentProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, TextBlock?>("TrimmedTipContent", typeof(TrimmedTextToolTip));

    private static bool installed;

    /// <summary>Hooks every TextBlock once per process; called while the application initializes.</summary>
    public static void Install()
    {
        if (installed)
        {
            return;
        }

        installed = true;
        Visual.BoundsProperty.Changed.AddClassHandler<TextBlock>(static (text, _) => Update(text));
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>(static (text, _) => Update(text));
    }

    /// <summary>True when the text's laid-out lines were cut to an ellipsis.</summary>
    public static bool IsTrimmed(TextBlock text)
    {
        if (text.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(text.Text))
        {
            return false;
        }

        foreach (var line in text.TextLayout.TextLines)
        {
            if (line.HasCollapsed)
            {
                return true;
            }
        }

        return false;
    }

    private static void Update(TextBlock text)
    {
        var owns = text.GetValue(OwnsTipProperty);
        if (!owns && (text.TextTrimming == TextTrimming.None || ToolTip.GetTip(text) is not null))
        {
            return;
        }

        if (IsTrimmed(text))
        {
            if (text.GetValue(TipContentProperty) is { } content)
            {
                content.Text = text.Text;
            }
            else
            {
                ToolTip.SetTip(text, ToolTipText.Wrap(text.Text, out content));
                text.SetValue(TipContentProperty, content);
                text.AddHandler(ToolTip.ToolTipOpeningEvent, OnTipOpening);
            }

            text.SetValue(OwnsTipProperty, true);
        }
        else if (owns)
        {
            text.ClearValue(ToolTip.TipProperty);
            text.ClearValue(OwnsTipProperty);
            text.SetValue(TipContentProperty, null);
        }
    }

    /// <summary>Undoes the owner's one-line layout that the tooltip service copied onto the tip's own text.</summary>
    private static void OnTipOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is TextBlock text && text.GetValue(TipContentProperty) is { } content)
        {
            content.SetValue(TextBlock.MaxLinesProperty, 0);
            content.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.None);
        }
    }
}
