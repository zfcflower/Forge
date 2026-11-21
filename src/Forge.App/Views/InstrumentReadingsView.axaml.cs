using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The one rendering of a strip's readings (quota, cache lifetime, the usage chip with its summary flyout, the context
/// gauge), hosted by the composer strip for the session and by a sub-agent's breadcrumb for that child.
/// <see cref="CompactCommand"/> is the host's own action for a gauge click - the session's manual compaction. A host
/// that has none (a sub-agent's tab, where compaction stays automatic) leaves it null: the gauge still shows its
/// numbers, and its tooltip says the compaction is automatic instead of promising a click that does nothing.
/// </summary>
public partial class InstrumentReadingsView : UserControl
{
    public static readonly StyledProperty<ICommand?> CompactCommandProperty =
        AvaloniaProperty.Register<InstrumentReadingsView, ICommand?>(nameof(CompactCommand));

    public ICommand? CompactCommand
    {
        get => GetValue(CompactCommandProperty);
        set => SetValue(CompactCommandProperty, value);
    }

    /// <summary>The gauge's whole hover line: the reading's numbers plus what a click does on this host. Both inputs
    /// (the host's command, the data context) are set once in XAML and never move, so a one-shot evaluation is the truth.</summary>
    public string GaugeTooltip => (DataContext as SessionInstrumentsViewModel) is { } readings
        ? readings.ContextTooltip + "\n" + (CompactCommand is null ? InstrumentsStrings.ContextAutoHint : InstrumentsStrings.ContextCompactHint)
        : CompactCommand is null ? InstrumentsStrings.ContextAutoHint : InstrumentsStrings.ContextCompactHint;

    public InstrumentReadingsView()
    {
        InitializeComponent();
    }
}
