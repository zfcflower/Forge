using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Forge.App.Views;

/// <summary>
/// An <see cref="ItemsControl"/> whose items take their controls from <see cref="RowControlPool"/>: a round's rows and a tool
/// group's cards are realized, dropped and realized again as the transcript scrolls, and building each one afresh is what makes
/// a drag cost a frame per round. The host returns its items' controls to the cache when it lets them go, both when the items
/// change (the base class clears each container's content) and when the whole host leaves the tree, which is how a round that
/// scrolled out hands its rows to the round that scrolls in.
/// </summary>
internal sealed class PooledItemsControl : ItemsControl
{
    public PooledItemsControl() => ItemTemplate = RowControlPool.Template(this);

    /// <summary>
    /// Which template family the items are drawn from — <see cref="RowScope.Card"/> for a tool group's cards, which
    /// <c>ToolGroupView</c> draws from its own templates rather than the application's. Never changes after the tree is built.
    /// </summary>
    public RowScope Scope { get; set; } = RowScope.Row;

    /// <summary>
    /// Drawn by <see cref="ItemsControl"/>'s own theme, which is what supplies the presenter: a control theme exists per style
    /// key, so without this the host would have no template and show nothing at all.
    /// </summary>
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override void ClearContainerForItemOverride(Control container)
    {
        var child = RowControlPool.ChildOf(container);
        var data = container.DataContext;
        base.ClearContainerForItemOverride(container);
        RowControlPool.Return(Scope, child, data);
    }

    /// <summary>
    /// A host leaving the tree is taking its items' controls with it, and nothing else will ever let them go: they are handed
    /// back here, while they are still built, so the next host of the same kind binds them to its own items instead of
    /// building them again.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Items is { } items)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (ContainerFromIndex(index) is { } container)
                {
                    ClearContainerForItemOverride(container);
                }
            }
        }

        base.OnDetachedFromVisualTree(e);
    }
}
