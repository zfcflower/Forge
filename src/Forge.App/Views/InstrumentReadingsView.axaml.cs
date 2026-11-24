using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The one rendering of a strip's readings (quota, cache lifetime, the usage chip with its summary flyout, the context
/// gauge), hosted by the composer strip for the session and by a sub-agent's breadcrumb for that child.
/// <see cref="CompactCommand"/> is the host's own action for a gauge click - the session's manual compaction. A host
/// that has none (a sub-agent's tab, where compaction stays automatic) leaves it null: the gauge still shows its
/// numbers, and its tooltip says the compaction is automatic instead of promising a click that does nothing.
/// <para>The gauge's hover line is composed here, not bound: it is the reading (which moves with every measurement) plus
/// what a click does on this host, so it is rewritten whenever either input changes. A binding to a one-shot property
/// froze the line at whatever the reading said when the binding was attached - a session that had not measured yet kept
/// saying so forever.</para>
/// </summary>
public partial class InstrumentReadingsView : UserControl
{

    /// <summary>The gauge's hover line, owned by this control and rewritten in place (see <see cref="ToolTipText"/>).</summary>
    private readonly TextBlock gaugeTip;

    /// <summary>The readings this control currently listens to; null between a detach and the next attach.</summary>
    private SessionInstrumentsViewModel? readings;

    static InstrumentReadingsView()
    {
        // The host's half of the line (click to compact, or a child's read-only gauge) is set in XAML and can change with it.
        CompactCommandProperty.Changed.AddClassHandler<InstrumentReadingsView>(static (view, _) => view.SyncGaugeTip());
    }

    [GeneratedStyledProperty]
    public partial ICommand? CompactCommand { get; set; }

    public InstrumentReadingsView()
    {
        // The block exists before the markup loads: the class handler above can fire while properties are still being set.
        gaugeTip = ToolTipText.Wrap(string.Empty);
        InitializeComponent();
        ToolTip.SetTip(Gauge, gaugeTip);
        SyncGaugeTip();
    }

    /// <summary>The gauge's whole hover line: the reading's own numbers, then what a click does on this host.</summary>
    private string ComposeGaugeTip() => (DataContext as SessionInstrumentsViewModel) is { } gauge
        ? gauge.ContextTooltip + "\n" + (CompactCommand is null ? InstrumentsStrings.ContextAutoHint : InstrumentsStrings.ContextCompactHint)
        : CompactCommand is null ? InstrumentsStrings.ContextAutoHint : InstrumentsStrings.ContextCompactHint;

    /// <summary>Rewrites the line from the current reading and host; the open tip re-renders with it.</summary>
    private void SyncGaugeTip() => gaugeTip.Text = ComposeGaugeTip();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Track();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Track();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A closed tab's view must not stay reachable through the session's instruments for the life of the session.
        Untrack();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Listens to the reading's own line, so a measurement (or a language switch) reaches the open tooltip.</summary>
    private void Track()
    {
        if (DataContext as SessionInstrumentsViewModel is not { } next)
        {
            Untrack();
            SyncGaugeTip();
            return;
        }

        if (!ReferenceEquals(next, readings))
        {
            Untrack();
            readings = next;
            next.PropertyChanged += OnReadingChanged;
        }

        SyncGaugeTip();
    }

    private void Untrack()
    {
        if (readings is { } current)
        {
            current.PropertyChanged -= OnReadingChanged;
            readings = null;
        }
    }

    private void OnReadingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SessionInstrumentsViewModel.ContextTooltip))
        {
            SyncGaugeTip();
        }
    }
}
