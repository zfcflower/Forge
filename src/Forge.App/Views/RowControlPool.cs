using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>Which templates a pooled control was built from: the application's row templates, or the tool group's card ones.</summary>
internal enum RowScope
{
    /// <summary>A transcript row (a round's row, or a list item): the templates in <c>App.axaml</c>.</summary>
    Row = 0,

    /// <summary>A card inside a tool group: the templates in <c>ToolGroupView</c>.</summary>
    Card = 1,
}

/// <summary>
/// The transcript's reuse cache: row controls that scrolled out, waiting for the next row of the same view model type. A row's
/// control costs 0.5–6.6 ms and 120 KB–2.3 MB to build (template, style match, bindings, text layout) and 0–0.6 ms and
/// 3–190 KB to re-bind to a different row, so a drag through a long transcript — every step realizes a round, and a round is
/// one tool group plus four other rows — pays the build price for every row it brings in unless the controls of the rows that
/// left are reused.
/// <para>
/// One cache for the process, because a control is built from the application's templates and knows nothing about the list it
/// ends up in: the rows of a closed session serve the next one. That only holds while a waiting control carries no data, so
/// <see cref="Return"/> clears the data context, the row types whose view keeps its view model in a field of its own are never
/// kept, and every count is capped — the cache can never keep a session alive, and it holds a viewport's worth of controls
/// rather than every row that ever scrolled out.
/// </para>
/// <para>UI thread only (controls are built, attached and dropped there).</para>
/// </summary>
internal static class RowControlPool
{
    /// <summary>How many controls of one view model type wait for reuse: a viewport shows about ten rows of a type.</summary>
    private const int MaxPerKey = 24;

    /// <summary>How many controls wait at all, across every scope and type.</summary>
    private const int MaxTotal = 120;

    private static readonly Dictionary<(RowScope Scope, Type Type), Stack<Control>> ByKey = [];

    private static int count;

    /// <summary>How many controls are waiting (the bench reads it).</summary>
    internal static int Count => count;

    /// <summary>How many controls the cache has handed out since the process started (the bench proves reuse with it).</summary>
    internal static int Rented { get; private set; }


    /// <summary>
    /// Off for a bench half that measures what building every row costs: nothing is rented and nothing is kept, so the same
    /// binary can be timed with and without the cache.
    /// </summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>How many waiting controls still carry a data context (the bench fails when one does).</summary>
    internal static int WaitingWithData
    {
        get
        {
            var carrying = 0;
            foreach (var waiting in ByKey.Values)
            {
                foreach (var control in waiting)
                {
                    carrying += control.DataContext is null ? 0 : 1;
                }
            }

            return carrying;
        }
    }

    /// <summary>
    /// The item template every row host uses: the waiting control for the item's type, or the template the tree has for it.
    /// </summary>
    /// <param name="host">The host whose trees are searched for the template to build a row nobody has built yet.</param>
    public static IDataTemplate Template(Control host) => new PooledTemplate(host);

    /// <summary>Which template family a host draws from: a tool group's card host says so, everything else draws rows.</summary>
    private static RowScope ScopeOf(Control host) => host is PooledItemsControl pooled ? pooled.Scope : RowScope.Row;

    /// <summary>The built child inside an item container, or null when the container shows nothing.</summary>
    public static Control? ChildOf(Control container) => container switch
    {
        // An ItemsControl's container is the presenter itself; a ListBox wraps it in the item control.
        ContentPresenter presenter => presenter.Child,
        ContentControl control => control.Presenter?.Child,
        _ => null,
    };

    /// <summary>Puts the container's row control back in the cache, where it can be bound to another row.</summary>
    public static void Return(RowScope scope, Control? control, object? data)
    {
        if (!Enabled || control is null || data is null || KeepsItsModel(data))
        {
            return;
        }

        var key = (scope, data.GetType());
        if (!ByKey.TryGetValue(key, out var waiting))
        {
            ByKey[key] = waiting = new Stack<Control>();
        }

        if (waiting.Count >= MaxPerKey || count >= MaxTotal)
        {
            return;
        }

        // A control the cache hands out must start where a freshly built one does. Re-binding the data context does not undo what
        // the last row did to the control itself: an inner viewer keeps the scroll position it was left at (a thinking block's
        // body, a tool call's arguments and result) and a selectable text keeps its selection. Both belong to the row that was
        // there, and neither is visible to the template that binds the next one.
        ForgetViewState(control);
        // No data, so a waiting control never keeps a view model (and its session) alive. Cleared, not set to null: a local null
        // would keep blocking the data context the next container hands down by inheritance.
        control.ClearValue(StyledElement.DataContextProperty);
        waiting.Push(control);
        count++;
    }

    /// <summary>
    /// Undoes what the row that used the control did to it: inner scroll positions and text selections. Cleared, not written to
    /// zero: both properties are coerced (a selection against the text's length, an offset against the extent), so on a detached
    /// control the value read back is already the coerced zero while the stored one is still the old row's - writing zero would
    /// be "no change" and the old value would return with the next text or extent.
    /// </summary>
    private static void ForgetViewState(Control control)
    {
        foreach (var visual in control.GetVisualDescendants())
        {
            switch (visual)
            {
                case ScrollViewer viewer:
                    viewer.ClearValue(ScrollViewer.OffsetProperty);
                    break;
                case SelectableTextBlock text:
                    text.ClearValue(SelectableTextBlock.SelectionStartProperty);
                    text.ClearValue(SelectableTextBlock.SelectionEndProperty);
                    break;
            }
        }
    }

    /// <summary>
    /// A reply's view (<see cref="ChatMarkdownView"/>) remembers the view model it rendered, so a waiting one would keep that
    /// row - and the session behind it - alive for as long as the cache holds it. Its reuse is also the least valuable
    /// (1.4 ms built against 1.0 ms re-bound), so it is simply not kept.
    /// </summary>
    private static bool KeepsItsModel(object data) => data is TurnTextViewModel;

    private static Control? Rent(RowScope scope, object data)
    {
        if (!Enabled || !ByKey.TryGetValue((scope, data.GetType()), out var waiting))
        {
            return null;
        }

        while (waiting.TryPop(out var control))
        {
            count--;
            // A control that is still in a tree is one nothing has emptied yet: leave it to its tree.
            if (control.GetVisualParent() is null)
            {
                Rented++;
                return control;
            }
        }

        return null;
    }

    private sealed class PooledTemplate(Control host) : IDataTemplate
    {
        /// <summary>Every row is built here, so the tree is searched for the one template that matches the row.</summary>
        public bool Match(object? data) => data is not null;

        public Control? Build(object? data) =>
            data is null
                ? null
                : Rent(ScopeOf(host), data) ?? host.FindDataTemplate(data)?.Build(data) ?? new TextBlock { Text = data.ToString() };
    }
}
