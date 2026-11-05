using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Forge.App.ViewModels;

/// <summary>An observable list with range operations that raise one change notification per call.</summary>
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var copy = new List<T>(items);
        for (var i = 0; i < copy.Count; i++)
        {
            Items.Insert(index + i, copy[i]);
        }

        Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)copy, index));
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }

        Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, (IList)removed, index));
    }

    private void Raise(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}

internal interface IFlatTreeNode<T> where T : class
{
    int Depth { get; }

    bool IsExpanded { get; }

    IReadOnlyList<T> Children { get; }
}

/// <summary>
/// A tree shown as one flat, virtualizable list of the currently visible rows. Expanding a node splices its visible
/// descendants in behind it, collapsing removes them; neither touches the rest of the list.
/// </summary>
internal sealed class FlatTree<T> where T : class, IFlatTreeNode<T>
{
    public RangeObservableCollection<T> Rows { get; } = [];

    /// <summary>Visible rows of <paramref name="roots"/>; safe to call off the UI thread on nodes that are not yet shown.</summary>
    public static List<T> Flatten(IReadOnlyList<T> roots)
    {
        var rows = new List<T>();
        foreach (var root in roots)
        {
            Append(rows, root);
        }

        return rows;
    }

    public void Replace(IReadOnlyList<T> rows) => Rows.ReplaceAll(rows);

    public void Clear() => Rows.ReplaceAll(Array.Empty<T>());

    public void ExpansionChanged(T node)
    {
        var index = Rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        if (node.IsExpanded)
        {
            var added = new List<T>();
            foreach (var child in node.Children)
            {
                Append(added, child);
            }

            Rows.InsertRange(index + 1, added);
            return;
        }

        var count = 0;
        while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > node.Depth)
        {
            count++;
        }

        Rows.RemoveRange(index + 1, count);
    }

    private static void Append(List<T> rows, T node)
    {
        rows.Add(node);
        if (!node.IsExpanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            Append(rows, child);
        }
    }
}

internal static class CollectionSync
{
    /// <summary>
    /// Brings <paramref name="target"/> to the content of <paramref name="source"/> with the fewest edits, keeping the
    /// existing instance wherever an equal one is already in place (so selection and scroll position survive).
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count && Equals(target[i], source[i]))
            {
                continue;
            }

            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (Equals(target[j], source[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                for (var k = found - 1; k >= i; k--)
                {
                    target.RemoveAt(k);
                }
            }
            else
            {
                target.Insert(i, source[i]);
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

/// <summary>Runs store and file-system reads on the thread pool so the dispatcher only ever applies finished results.</summary>
internal static class BackgroundRead
{
    public static Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct) =>
        Task.Run(() => read(ct), ct);

    public static Task RunAsync(Func<CancellationToken, Task> read, CancellationToken ct) =>
        Task.Run(() => read(ct), ct);

}
