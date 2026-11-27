using Avalonia.Controls;
using Forge.Abstractions;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// The session-level turn ledger observation window (stage one, shadow mode): every row of one session, oldest turn
/// first, with a per-row status badge and the footer's compliance counts. Read-only, rebuilt on every open.
/// </summary>
internal sealed partial class TurnLedgerWindow : Window
{
    /// <summary>Designer/loader constructor.</summary>
    public TurnLedgerWindow() => InitializeComponent();

    internal TurnLedgerWindow(TurnLedgerWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    /// <summary>Shows the session's ledger over <paramref name="owner"/>; the rows are read before the dialog opens.</summary>
    public static Task ShowAsync(Window owner, IReadOnlyList<TurnLedgerEntry> rows)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rows);
        return new TurnLedgerWindow(new TurnLedgerWindowViewModel(rows)).ShowDialog(owner);
    }
}
