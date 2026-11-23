using System;
using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The transcript's list. It is a <see cref="ListBox"/> (and styled as one) whose item containers are recycled by the
/// virtualizing panel; the controls inside them are recycled too, through <see cref="RowControlPool"/>: Avalonia empties a
/// container's content when it is recycled, so without the cache every round that scrolled in built its controls again.
/// </summary>
internal sealed class TranscriptListBox : ListBox
{
    public TranscriptListBox() => ItemTemplate = RowControlPool.Template(this);

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void ClearContainerForItemOverride(Control container)
    {
        // Called just before the generator empties the container's content, which would drop the round's controls.
        var child = RowControlPool.ChildOf(container);
        var data = container.DataContext;
        base.ClearContainerForItemOverride(container);
        RowControlPool.Return(RowScope.Row, child, data);
    }
}
