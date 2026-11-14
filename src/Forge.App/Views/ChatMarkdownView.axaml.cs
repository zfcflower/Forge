using Avalonia;
using Avalonia.Controls;
using Forge.App.Changes.Markdown;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Renders a streaming reply as Markdown. The view model hands over a <see cref="StreamingMarkdown"/> document: a committed
/// block prefix that is published once, and a live tail that is re-parsed on every append. This view keeps one control per
/// block, appends a control when a block is committed, and refreshes only the last live block - in place for the two kinds it
/// can be (a paragraph or heading gets new inlines, a code block gets new text), rebuilt only when the block's shape changed
/// (a list or a table that grew). A whole reply is therefore never re-laid out: the cost of a flush is the size of the tail.
/// <para>The rendered tree is kept while the view is detached (a session in a background tab keeps streaming): the
/// subscription pauses, and the next attach catches up through <see cref="Sync"/>. That also keeps the view out of the view
/// model's lifetime once the tab is gone.</para>
/// </summary>
public partial class ChatMarkdownView : UserControl
{
    private readonly List<(MdBlock Block, Control Control)> blocks = [];
    private TurnTextViewModel? model;
    private int committed;
    private bool listening;

    public ChatMarkdownView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen();
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Silence();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var wasListening = listening;
        Silence();
        Bind(DataContext as TurnTextViewModel);
        if (wasListening)
        {
            Listen();
        }
    }

    private void Bind(TurnTextViewModel? next)
    {
        if (ReferenceEquals(model, next))
        {
            return;
        }

        model = next;
        Rebuild();
    }

    private void Listen()
    {
        if (model is null)
        {
            return;
        }

        model.ContentChanged -= OnContentChanged;
        model.ContentChanged += OnContentChanged;
        listening = true;
    }

    private void Silence()
    {
        if (!listening)
        {
            return;
        }

        model?.ContentChanged -= OnContentChanged;
        listening = false;
    }

    private void OnContentChanged(object? sender, EventArgs e) => Sync();

    private void Rebuild()
    {
        Body.Children.Clear();
        blocks.Clear();
        committed = 0;
        Sync();
    }

    private void Sync()
    {
        if (model is null)
        {
            return;
        }

        var stable = model.Document.Stable;
        if (stable.Count < committed || !PrefixMatches(stable))
        {
            Rebuild();
            return;
        }

        if (stable.Count > committed)
        {
            // The tail becomes part of the prefix; dropping it here keeps the committed blocks first in the panel.
            Truncate(committed);
            for (var index = committed; index < stable.Count; index++)
            {
                Add(stable[index]);
            }

            committed = stable.Count;
        }

        SyncLive(model.Document.Live);
    }

    private bool PrefixMatches(IReadOnlyList<MdBlock> stable)
    {
        for (var index = 0; index < committed; index++)
        {
            if (!StreamingMarkdown.SameBlock(blocks[index].Block, stable[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Refreshes the tail, rebuilding only from the first live block that is no longer the block it was.</summary>
    private void SyncLive(IReadOnlyList<MdBlock> live)
    {
        var last = live.Count - 1;
        for (var index = 0; index < live.Count; index++)
        {
            var position = committed + index;
            if (position >= blocks.Count || !Reusable(blocks[position].Block, live[index], index == last))
            {
                Truncate(position);
                for (var tail = index; tail < live.Count; tail++)
                {
                    Add(live[tail]);
                }

                return;
            }

            if (index == last)
            {
                Refresh(position, live[index]);
            }
        }

        Truncate(committed + live.Count);
    }

    /// <summary>
    /// Whether a rendered control can serve the next parse of the same block. Everything but the tail must be the identical
    /// block; the tail may still grow (a code block gaining a line, a paragraph gaining text), so for it only the kind and the
    /// first line matter - <see cref="Refresh"/> replaces its whole content anyway.
    /// </summary>
    private static bool Reusable(MdBlock rendered, MdBlock next, bool isLast) =>
        isLast
            ? rendered.Kind == next.Kind && rendered.StartLine == next.StartLine
            : StreamingMarkdown.SameBlock(rendered, next);

    /// <summary>
    /// Re-renders the last live block: a growing paragraph or heading only needs new inlines and a code block only new text,
    /// so the control survives the flush; anything else is replaced.
    /// </summary>
    private void Refresh(int position, MdBlock block)
    {
        var control = blocks[position].Control;
        if (block.Kind is MdBlockKind.Paragraph or MdBlockKind.Heading && control is MarkdownTextBlock text)
        {
            text.SetInlines(block.Inlines);
            blocks[position] = (block, control);
            return;
        }

        if (block.Kind is MdBlockKind.Code or MdBlockKind.Html && control is MarkdownCodeBlock code)
        {
            code.Update(block.Text, block.Info);
            blocks[position] = (block, control);
            return;
        }

        Truncate(position);
        Add(block);
    }

    private void Add(MdBlock block)
    {
        var control = MarkdownRenderer.Render(block);
        blocks.Add((block, control));
        Body.Children.Add(control);
    }

    private void Truncate(int count)
    {
        if (blocks.Count <= count)
        {
            return;
        }

        blocks.RemoveRange(count, blocks.Count - count);
        Body.Children.RemoveRange(count, Body.Children.Count - count);
    }
}
