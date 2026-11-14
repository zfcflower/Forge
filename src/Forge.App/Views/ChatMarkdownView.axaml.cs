using Avalonia;
using Avalonia.Controls;
using Forge.App.Changes.Markdown;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Renders a streaming reply as Markdown. The view model hands over a <see cref="StreamingMarkdown"/> document: a committed
/// block prefix that is published once, and a live tail that is re-parsed on every append. This view keeps one control per
/// block, appends a control when a block is committed, and refreshes only the last live block - in place for the two kinds it
/// can be (a paragraph or heading gets new inlines, a code block gets new text, a list or table keeps the rows that came from
/// ended lines and rebuilds only the last one), rebuilt only when the block's kind changed. A whole reply is therefore never
/// re-laid out: the cost of a flush is the size of the tail.
/// <para>The rendered tree is kept while the view is detached (a session in a background tab keeps streaming): the
/// subscription pauses, and the next attach catches up through <see cref="Sync"/>. That also keeps the view out of the view
/// model's lifetime once the tab is gone.</para>
/// </summary>
public partial class ChatMarkdownView : UserControl
{
    private readonly List<(MdBlock Block, Control Control, int CompleteLines)> blocks = [];
    private TurnTextViewModel? model;
    private int committed;
    private int revision;
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
        revision = model?.Document.Revision ?? 0;
        Sync();
    }

    private void Sync()
    {
        if (model is null)
        {
            return;
        }

        // The committed prefix only ever grows, except when completion replaced it; the revision says which, without walking it.
        var stable = model.Document.Stable;
        if (model.Document.Revision != revision || stable.Count < committed)
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
    /// Re-renders the last live block: a growing paragraph or heading only needs new inlines, a code block only new text, and
    /// a list or table keeps the rows that came from ended lines and rebuilds only the rest, so the control survives the flush;
    /// anything else is replaced.
    /// </summary>
    private void Refresh(int position, MdBlock block)
    {
        var (rendered, control, completeLines) = blocks[position];
        var now = model?.Document.CompleteLines ?? 0;
        if (block.Kind is MdBlockKind.Paragraph or MdBlockKind.Heading && control is MarkdownTextBlock text)
        {
            text.SetInlines(block.Inlines);
            blocks[position] = (block, control, now);
            return;
        }

        if (block.Kind is MdBlockKind.Code or MdBlockKind.Html && control is MarkdownCodeBlock code)
        {
            // The fence's info string can arrive in pieces ("```" then "mermaid"): the block that started as plain code
            // becomes a Mermaid block as soon as the info says so, instead of waiting for the reply to be rebuilt.
            if (MarkdownRenderer.IsMermaid(MarkdownRenderer.CodeInfo(block)))
            {
                Truncate(position);
                Add(block);
                return;
            }

            code.Update(block.Text, MarkdownRenderer.CodeInfo(block));
            blocks[position] = (block, control, now);
            return;
        }

        if (block.Kind is MdBlockKind.Code && control is MermaidBlockView mermaid)
        {
            // The block decides what to do with an open fence (show the source) and with a closed one (render); the view only
            // hands it the current text and whether the fence just closed.
            mermaid.Update(block.Text, MarkdownRenderer.CodeInfo(block), block.FenceClosed);
            blocks[position] = (block, control, now);
            return;
        }

        if (MarkdownRenderer.TryGrow(control, rendered, block, completeLines))
        {
            blocks[position] = (block, control, now);
            return;
        }

        Truncate(position);
        Add(block);
    }

    private void Add(MdBlock block)
    {
        var control = MarkdownRenderer.Render(block);
        if (blocks.Count > 0 && IsFragment(blocks[^1].Block, block))
        {
            // A paragraph committed in fragments (an over-long one, see StreamingMarkdown) reads as one paragraph while it
            // streams: the panel's block spacing would otherwise open a gap at every fragment line.
            control.Margin = new Thickness(0, -Body.Spacing, 0, 0);
        }

        blocks.Add((block, control, model?.Document.CompleteLines ?? 0));
        Body.Children.Add(control);
    }

    /// <summary>Two paragraphs on adjacent lines are fragments of one: a single parse never produces them.</summary>
    private static bool IsFragment(MdBlock previous, MdBlock block) =>
        previous.Kind == MdBlockKind.Paragraph && block.Kind == MdBlockKind.Paragraph && block.StartLine == previous.EndLine + 1;

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
