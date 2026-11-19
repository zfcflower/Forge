using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The transcript's list. It is a <see cref="ListBox"/> (and styled as one) that keeps the controls of a row after the row
/// scrolls out and hands them to the next row of the same type. Avalonia already recycles the <c>ListBoxItem</c> containers,
/// but empties a container's content when it is recycled, so every row that scrolled in used to build its template again
/// (up to 100 KB of allocation and about a millisecond of the frame per row). A row's controls are bound to its view model
/// and carry no state of their own, so the next row only needs a new data context.
/// </summary>
internal sealed class TranscriptListBox : ListBox
{
    private readonly RowControlPool pool = new();

    public TranscriptListBox()
    {
        ItemTemplate = new PooledRowTemplate(this, pool);
    }

    protected override Type StyleKeyOverride => typeof(ListBox);

    /// <summary>How many rows' controls are kept for reuse, in total (the bench reads it).</summary>
    internal int PooledCount => pool.Count;

    protected override void ClearContainerForItemOverride(Control container)
    {
        // Called just before the generator empties the container's content, which would drop the row's controls.
        if (container is ContentControl { Presenter.Child: { } child })
        {
            pool.Return(child);
        }

        base.ClearContainerForItemOverride(container);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            // The controls still point at the old list's rows.
            pool.Clear();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        pool.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The row controls waiting for a row of their view model type. Used on the UI thread only.</summary>
    private sealed class RowControlPool
    {
        /// <summary>
        /// More than the rows a viewport shows of one type (the bench's 900 px window realizes about 10): when a thumb jump
        /// replaces the whole viewport the pool refills from the old rows, and a few spare absorb the next jump.
        /// </summary>
        private const int MaxPerType = 24;

        private readonly Dictionary<Type, Stack<Control>> byType = new();

        public int Count { get; private set; }

        /// <summary>
        /// The row types whose controls are reused: the rows that dominate a long transcript and whose views keep nothing
        /// but their data context. Prose (<c>TurnText</c>) builds a markdown tree that a new data context rebuilds anyway.
        /// </summary>
        public static bool Poolable(object? data) =>
            data is UserMessageViewModel or TurnCardViewModel or TurnThinkingViewModel or ToolRowViewModel or TurnDividerViewModel;

        public void Return(Control control)
        {
            var data = control.DataContext;
            if (!Poolable(data))
            {
                return;
            }

            var type = data!.GetType();
            if (!byType.TryGetValue(type, out var stack))
            {
                byType[type] = stack = new Stack<Control>();
            }

            if (stack.Count < MaxPerType)
            {
                stack.Push(control);
                Count++;
            }
        }

        public Control? Rent(object data)
        {
            if (byType.TryGetValue(data.GetType(), out var stack))
            {
                // A control still in a tree is one the generator has not emptied yet: leave it.
                while (stack.TryPop(out var control))
                {
                    Count--;
                    if (control.GetVisualParent() is null)
                    {
                        return control;
                    }
                }
            }

            return null;
        }

        public void Clear()
        {
            byType.Clear();
            Count = 0;
        }
    }

    /// <summary>What the list would do without an item template (the data template found for the row), plus the pool.</summary>
    private sealed class PooledRowTemplate(TranscriptListBox host, RowControlPool pool) : IDataTemplate
    {
        public bool Match(object? data) => data is not null;

        public Control? Build(object? data)
        {
            if (data is null)
            {
                return null;
            }

            if (RowControlPool.Poolable(data) && pool.Rent(data) is { } reused)
            {
                return reused;
            }

            return host.FindDataTemplate(data)?.Build(data) ?? new TextBlock { Text = data.ToString() };
        }
    }
}
