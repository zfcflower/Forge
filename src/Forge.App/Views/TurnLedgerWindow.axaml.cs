using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The session's managed context, as a timeline: one node per turn (「第 N 轮 — 用户输入」) with that turn's card under it — the
/// conclusion and the evidence the harness recorded. Read-only, and read a page at a time like the transcript: the newest page
/// on open, older ones as the reader scrolls to the top, each landing in front of what is already there while the list's
/// panel keeps the reader's place.
/// </summary>
internal sealed partial class TurnLedgerWindow : Window
{
    /// <summary>How many screens above the top the next page is asked for, so it lands while the reader is still scrolling.</summary>
    private const double PrefetchRunway = 2;

    private readonly TurnLedgerWindowViewModel? model;

    /// <summary>
    /// Whether the next near-the-top scroll may read: a page is asked for only once the list has moved or grown by a viewport
    /// since it was last placed, so the layout that draws the open page (offset 0, extent climbing) is not mistaken for the
    /// reader scrolling up. Same discipline as the transcript's own prefetch (<see cref="TranscriptFollower"/>).
    /// </summary>
    private bool armed;

    /// <summary>
    /// Whether the open's placement has happened. Layout runs before a background post, so the first page's own layout is
    /// measured against a baseline of zero: without this gate that layout looks like a viewport of growth and would read more
    /// history before the window has even reached its newest turn.
    /// </summary>
    private bool placed;

    /// <summary>How many times the open's placement has been re-posted while the list had nothing measured yet.</summary>
    private int placements;

    /// <summary>The offset and extent the last placement left, which is what "moved" and "grew" are measured against.</summary>
    private double placedOffset;
    private double placedExtent;

    /// <summary>Designer/loader constructor.</summary>
    public TurnLedgerWindow() => InitializeComponent();

    internal TurnLedgerWindow(TurnLedgerWindowViewModel viewModel)
        : this()
    {
        model = viewModel;
        DataContext = viewModel;
        // The list's scroller is the routed event's source, so the window reaches the scroll without digging into the
        // ListBox's template (the scroller only exists once the list has been laid out).
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);
    }

    /// <summary>Shows one session's ledger over <paramref name="owner"/>, reading its newest page on open.</summary>
    public static Task ShowAsync(Window owner, TurnLedgerPageReader read)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(read);
        var window = new TurnLedgerWindow(new TurnLedgerWindowViewModel(read));
        window.Opened += (_, _) => _ = window.LoadFirstPageAsync();
        return window.ShowDialog(owner);
    }

    /// <summary>
    /// The open: one page, then the list is put at its end — the newest turn, where the transcript also stands when a session
    /// opens. Nothing is read before that placement, so opening a long session costs exactly one page.
    /// </summary>
    private async Task LoadFirstPageAsync()
    {
        if (model is not { } ledger)
        {
            return;
        }

        await ReadAsync(ledger).ConfigureAwait(true);
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>Places the list at the newest turn and treats that as the placement later scrolls are measured against.</summary>
    private void ScrollToEnd()
    {
        if (Scroller() is not { } scroller)
        {
            return;
        }

        // Nothing measured yet (the window opened before its first layout): ask again once it has, a bounded number of times
        // - an empty timeline never gets an extent, and a scroll with nothing to scroll to is not a placement.
        if (scroller.Extent.Height <= 0 && model is { Rows.Count: > 0 } && ++placements <= 3)
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
            return;
        }

        scroller.ScrollToEnd();
        Place(scroller);
        placed = true;
        // A page that does not fill the viewport leaves nothing to scroll: its own arrival is the only chance to ask for the
        // next one, and no scroll event will ever come.
        armed = scroller.Extent.Height <= scroller.Viewport.Height;
        if (armed)
        {
            _ = LoadAsync();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer scroller)
        {
            Prefetch(scroller);
        }
    }

    /// <summary>The retry the failure line offers: the same read the prefetch asks for, asked again.</summary>
    private void OnRetryClick(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>
    /// Asks for the page above what is on screen once the reader is inside the runway. The arming test is what keeps the
    /// first layout and a prepended page from reading more history on their own: only a viewport of real movement (or a
    /// timeline too short to scroll) arms it.
    /// </summary>
    private void Prefetch(ScrollViewer scroller)
    {
        if (!placed || model is not { HasOlder: true, IsLoading: false } || scroller.Viewport.Height <= 0)
        {
            return;
        }

        if (!armed)
        {
            var shortTimeline = scroller.Extent.Height <= scroller.Viewport.Height;
            var moved = Math.Abs(scroller.Offset.Y - placedOffset) >= scroller.Viewport.Height;
            var grew = scroller.Extent.Height - placedExtent >= scroller.Viewport.Height;
            if (!shortTimeline && !moved && !grew)
            {
                return;
            }

            armed = true;
        }

        if (scroller.Offset.Y > PrefetchRunway * scroller.Viewport.Height)
        {
            return;
        }

        armed = false;
        Place(scroller);
        _ = LoadAsync();
    }

    /// <summary>
    /// Reads pages while the reader stays inside the runway. A page that adds no row ends the round trip (a store answering
    /// nothing, or the model refusing a read already in flight) so nothing here can spin; after a page lands, its placement is
    /// what the next arming test measures against, and a timeline still too short to scroll keeps reading on its own.
    /// </summary>
    private async Task LoadAsync()
    {
        if (model is not { } ledger)
        {
            return;
        }

        while (ledger.HasOlder)
        {
            var before = ledger.Rows.Count;
            await ReadAsync(ledger).ConfigureAwait(true);
            if (ledger.Rows.Count == before || Scroller() is not { } scroller)
            {
                return;
            }

            Place(scroller);
            armed = scroller.Extent.Height <= scroller.Viewport.Height;
            if (!armed && scroller.Offset.Y > PrefetchRunway * scroller.Viewport.Height)
            {
                return;
            }
        }
    }

    /// <summary>One page's read; a failure costs that page and leaves the cursor where it was, so the retry (or a later scroll) asks again.</summary>
    private static async Task ReadAsync(TurnLedgerWindowViewModel ledger)
    {
        try
        {
            await ledger.LoadOlderAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"turn ledger window: {exception.Message}");
        }
    }

    private void Place(ScrollViewer scroller)
    {
        placedOffset = scroller.Offset.Y;
        placedExtent = scroller.Extent.Height;
    }

    private ScrollViewer? Scroller() => Timeline.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
}
