using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Keeps what the user is reading in a paused transcript at the same place on screen while rows change around it (a row
/// opens or closes, rows are inserted or removed above, a row grows). Without it the list jumps: the virtualizing panel
/// (Avalonia 12.1.6) forgets where its first realized row starts as soon as a realized row changes height or rows come or go
/// before it, guesses the visible index again from the average row height, and shows rows dozens of positions away at the
/// same scroll offset; the scroll presenter's own anchoring cannot help, because its anchor element is recycled by that
/// guess. The anchor works on top of both, in the spirit of CSS scroll anchoring:
/// <list type="bullet">
/// <item>It remembers one anchor: the row crossing the top of the viewport, or the button the user just clicked inside a
/// row (the thing under the pointer must stay under it, so the same button can be clicked again). A row is identified by its
/// item, never by its index or container; a clicked button by its command, because a recycled row gets a new button.</item>
/// <item>After every layout pass (after the presenter's own anchoring has run) it measures where the anchor is now and moves
/// the offset by the remaining difference only, so nothing is corrected twice. When the panel's guess left the anchor row
/// unrealized, the row is brought back with <c>ScrollIntoView</c> and then put at its old place, in the same frame.</item>
/// <item>A removed anchor row hands over to its turn's header, placed at its old place or at the top edge. Without either
/// the anchor gives up rather than guess. At most <see cref="MaxCorrections"/> writes follow one change.</item>
/// <item>It stands down while the list follows the reply (the follower owns the offset then) and while the user scrolls
/// (wheel, keys, scroll bar): a scroll moves the anchor with the content, and the next pass picks a new one.</item>
/// </list>
/// </summary>
internal sealed class TranscriptScrollAnchor
{
    /// <summary>A movement at or under this is no movement (layout rounds to device pixels).</summary>
    private const double Tolerance = 0.5;

    /// <summary>Offset writes allowed for one change before the anchor accepts where things are.</summary>
    private const int MaxCorrections = 4;

    private readonly ListBox list;
    private readonly Func<bool> following;
    private ScrollViewer? scroller;

    private object? row;
    private Control? container;
    private ICommand? command;
    private double rowY;
    private double elementY;
    private double offset;
    private object? fallback;
    private double fallbackY;

    private bool changed;
    private bool userScrolling;
    private bool scrolledByUser;
    private bool busy;
    private bool ownWrite;
    private int attempts;

    public TranscriptScrollAnchor(ListBox list, Func<bool> following)
    {
        this.list = list;
        this.following = following;
        list.LayoutUpdated += OnLayoutUpdated;
        list.AddHandler(Button.ClickEvent, OnClick, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Off only for measurements of the list without it (the bench's baseline); the app never turns it off.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>Time spent holding the anchor, layout passes run by a restore included (diagnostics).</summary>
    public TimeSpan Busy { get; private set; }

    /// <summary>Offset writes made to hold an anchor (diagnostics).</summary>
    public int Corrections { get; private set; }

    /// <summary>Anchors brought back from an unrealized row with <c>ScrollIntoView</c> (diagnostics).</summary>
    public int Restores { get; private set; }

    /// <summary>Changes after which the anchor could not be found or held and was given up (diagnostics).</summary>
    public int GiveUps { get; private set; }

    public void SetScroller(ScrollViewer? value)
    {
        scroller = value;
        Clear();
    }

    /// <summary>Rows were added, removed or replaced. A reset (another conversation, a cleared one) has nothing to hold.</summary>
    public void ItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            Clear();
        }
        else
        {
            changed = true;
        }
    }

    /// <summary>The user turned the wheel or pressed a scrolling key: the next pass takes a new anchor instead of holding the old.</summary>
    public void UserScrolled() => scrolledByUser = true;

    /// <summary>The user pressed the scroll bar; until the button is released the anchor only follows the content.</summary>
    public void BeginUserScroll() => userScrolling = true;

    public void EndUserScroll()
    {
        if (userScrolling)
        {
            userScrolling = false;
            scrolledByUser = true;
        }
    }

    /// <summary>
    /// Whether the scroll change being reported is the anchor's own write, consumed by the call: the follower must not read
    /// a correction that happens to land at the bottom as the user returning there.
    /// </summary>
    public bool TakeOwnWrite()
    {
        var own = ownWrite;
        ownWrite = false;
        return own;
    }

    private void Clear()
    {
        row = null;
        container = null;
        command = null;
        fallback = null;
        changed = false;
        attempts = 0;
    }

    /// <summary>
    /// A button inside a row was clicked. The click is raised before the button runs its command, so this is the state the
    /// user saw: the button becomes the anchor until the change it makes has been laid out.
    /// </summary>
    private void OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (busy || scroller is not { } sv || following() || e.Source is not Button button || button.Command is null)
        {
            return;
        }

        if (button.FindAncestorOfType<ListBoxItem>() is not { } item || list.ItemFromContainer(item) is not { } clicked ||
            Y(item, sv) is not { } top || Y(button, sv) is not { } at)
        {
            return;
        }

        Take(clicked, item, top, sv);
        command = button.Command;
        elementY = at;
        changed = true;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (busy || !Enabled || scroller is not { } sv)
        {
            return;
        }

        if (following())
        {
            Clear();
            return;
        }

        if (userScrolling || scrolledByUser || row is null)
        {
            scrolledByUser = false;
            Snapshot(sv);
            return;
        }

        busy = true;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Hold(sv);
        }
        finally
        {
            busy = false;
            Busy += System.Diagnostics.Stopwatch.GetElapsedTime(start);
        }
    }

    private void Hold(ScrollViewer sv)
    {
        var scrolled = sv.Offset.Y - offset;
        if (Locate(sv) is var (now, target))
        {
            var shift = now - target;
            if (Math.Abs(shift) <= Tolerance)
            {
                // In place. Keep the anchor (no work) unless the offset moved under it, which means it is a new frame of reference.
                if (Math.Abs(scrolled) > Tolerance || changed)
                {
                    Snapshot(sv);
                }

                return;
            }

            // The anchor moved with the content of a scroll (nothing changed in the rows, or the move is exactly the scroll):
            // a programmatic scroll the anchor must not undo.
            if (!changed && Math.Abs(shift + scrolled) <= 1)
            {
                Snapshot(sv);
                return;
            }

            Correct(sv, shift);
            return;
        }

        // The anchor row is not realized. With the offset moved and nothing changed in the rows, a scroll took it away.
        if (!changed && Math.Abs(scrolled) > Tolerance)
        {
            Snapshot(sv);
            return;
        }

        Restore(sv);
    }

    /// <summary>Where the anchor is now and where it should be, or null when its row is not realized.</summary>
    private (double Now, double Target)? Locate(ScrollViewer sv)
    {
        if (!IsRealized(container) || !ReferenceEquals(list.ItemFromContainer(container!), row))
        {
            container = list.ContainerFromItem(row!);
            if (!IsRealized(container))
            {
                container = null;
                return null;
            }
        }

        if (command is not null && FindButton(container!, command) is { } button && Y(button, sv) is { } at)
        {
            return (at, elementY);
        }

        // No clicked button (or it is gone with its template): the row's top holds.
        return Y(container!, sv) is { } top ? (top, rowY) : null;
    }

    /// <summary>Brings the anchor row (or its turn header, when the row left the list) back from the panel's guess.</summary>
    private void Restore(ScrollViewer sv)
    {
        if (attempts >= MaxCorrections || list.ItemsSource is not IList rows)
        {
            GiveUp(sv);
            return;
        }

        var index = rows.IndexOf(row!);
        if (index < 0)
        {
            // The row itself went away (its turn collapsed). Its header takes over, at its old place or at the top edge.
            if (fallback is null || (index = rows.IndexOf(fallback)) < 0)
            {
                GiveUp(sv);
                return;
            }

            row = fallback;
            rowY = double.IsNaN(fallbackY) ? 0 : Math.Max(fallbackY, 0);
            command = null;
            fallback = null;
        }

        attempts++;
        Restores++;
        list.ScrollIntoView(index);
        container = list.ContainerFromIndex(index);
        if (Locate(sv) is not var (now, wanted))
        {
            GiveUp(sv);
            return;
        }

        Write(sv, now - wanted);
    }

    private void Correct(ScrollViewer sv, double shift)
    {
        if (attempts >= MaxCorrections)
        {
            GiveUp(sv);
            return;
        }

        attempts++;
        Write(sv, shift);
    }

    private void Write(ScrollViewer sv, double shift)
    {
        if (Math.Abs(shift) <= Tolerance)
        {
            Snapshot(sv);
            return;
        }

        var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        var to = Math.Clamp(sv.Offset.Y + shift, 0, max);
        if (Math.Abs(to - sv.Offset.Y) <= Tolerance)
        {
            // Pinned at an end of the extent: the content cannot be put back, accept it.
            GiveUp(sv);
            return;
        }

        Corrections++;
        ownWrite = true;
        sv.Offset = new Vector(sv.Offset.X, to);
        offset = to;
    }

    private void GiveUp(ScrollViewer sv)
    {
        GiveUps++;
        Snapshot(sv);
    }

    /// <summary>Takes the row crossing the top edge of the viewport (or the first below it) as the new anchor.</summary>
    private void Snapshot(ScrollViewer sv)
    {
        Clear();
        Control? best = null;
        var bestY = double.PositiveInfinity;
        var height = sv.Viewport.Height;
        foreach (var candidate in list.GetRealizedContainers())
        {
            if (!candidate.IsVisible || Y(candidate, sv) is not { } top || top >= height || top + candidate.Bounds.Height <= 0 || top >= bestY)
            {
                continue;
            }

            best = candidate;
            bestY = top;
        }

        if (best is not null && list.ItemFromContainer(best) is { } item)
        {
            Take(item, best, bestY, sv);
        }
    }

    private void Take(object item, Control itemContainer, double top, ScrollViewer sv)
    {
        Clear();
        row = item;
        container = itemContainer;
        rowY = top;
        offset = sv.Offset.Y;
        fallback = item switch
        {
            TurnBlockViewModel { Turn: { } turn } => turn,
            TurnFootnoteViewModel footnote => footnote.Card,
            _ => null,
        };
        fallbackY = fallback is not null && list.ContainerFromItem(fallback) is { } header && IsRealized(header) && Y(header, sv) is { } y ? y : double.NaN;
    }

    private bool IsRealized(Control? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        // Only the panel's realized range is laid out where it shows; the focused row it keeps alive outside the range is
        // arranged at an estimate and would be a false anchor.
        foreach (var realized in list.GetRealizedContainers())
        {
            if (ReferenceEquals(realized, candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static Button? FindButton(Control root, ICommand command)
    {
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is Button button && ReferenceEquals(button.Command, command) && button.IsEffectivelyVisible)
            {
                return button;
            }
        }

        return null;
    }

    private static double? Y(Visual visual, ScrollViewer sv) =>
        visual.IsAttachedToVisualTree() && visual.TranslatePoint(default, sv) is { } point ? point.Y : null;
}
