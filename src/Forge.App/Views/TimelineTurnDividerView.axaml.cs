using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The round-end divider: a wavy rule across the content column with the turn's metrics centred on it, on a row of its own
/// after the footnote. The axis stops on the row's closing node, so every turn's axis segment stands alone.
/// </summary>
public partial class TimelineTurnDividerView : UserControl
{
    public TimelineTurnDividerView() => InitializeComponent();

    /// <summary>
    /// The line is a computed property raised through <c>RefreshText</c>; a row realized from the pool (or realized
    /// between an update and its binding pass) reads fresh on attach, so no divider can stay stale from a missed raise.
    /// </summary>
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is ViewModels.TurnDividerViewModel divider)
        {
            divider.RefreshText();
        }
    }
}
