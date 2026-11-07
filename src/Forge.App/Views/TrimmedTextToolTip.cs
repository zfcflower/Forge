using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Views;

/// <summary>
/// Gives every trimmed <see cref="TextBlock"/> a tooltip with its full text: a text that fits shows none, one cut to an
/// ellipsis by a narrow column shows the whole of it on hover. Only tooltips this helper set are touched, so a text with
/// a tooltip of its own keeps it.
/// </summary>
internal static class TrimmedTextToolTip
{
    private static readonly AttachedProperty<bool> OwnsTipProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("OwnsTrimmedTip", typeof(TrimmedTextToolTip));

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
            ToolTip.SetTip(text, text.Text);
            text.SetValue(OwnsTipProperty, true);
        }
        else if (owns)
        {
            text.ClearValue(ToolTip.TipProperty);
            text.ClearValue(OwnsTipProperty);
        }
    }
}
