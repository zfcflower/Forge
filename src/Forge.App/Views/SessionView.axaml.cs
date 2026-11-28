using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class SessionView : UserControl
{
    private SessionWorkspaceViewModel? viewModel;
    private INotifyCollectionChanged? items;
    private TranscriptFollower? follower;

    public SessionView()
    {
        InitializeComponent();
        var input = this.FindControl<TextBox>("Input");
        if (input is not null)
        {
            // Tunnel, so Enter, Tab and the arrows reach the pickers before the text box consumes them.
            input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            // A text box selects a word on a double click; the third click selects the whole draft, the way an editor does.
            input.AddHandler(PointerPressedEvent, OnInputPointerPressed, RoutingStrategies.Tunnel);
            input.PropertyChanged += OnInputPropertyChanged;
            input.LostFocus += OnInputLostFocus;
            AttachImageInput(input);
        }

        if (this.FindControl<ListBox>("Transcript") is { } transcript)
        {
            follower = TranscriptFollower.Attach(transcript, LoadOlderAsync);
        }
    }

    /// <summary>Triple click in the composer selects everything; the text box already handles one and two clicks.</summary>
    private static void OnInputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 3 && sender is TextBox box)
        {
            box.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>
    /// The strip's resource chip: a menu, because the place that answers "what is this session made of" now carries both
    /// the resource window and the session's ledger timeline. Its items are built on every open from the strip's own state.
    /// </summary>
    private void OnResourcesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && DataContext is SessionWorkspaceViewModel model)
        {
            ResourceMenu(model.Instruments, read => _ = ShowSessionLedgerAsync(read)).ShowAt(button);
        }
    }

    /// <summary>
    /// The resource chip's menu for one strip: the window's entry always, then the session's ledger timeline only when the
    /// host wired a ledger source - no source, no entry (not a disabled one). Built per open rather than declared in the
    /// XAML, the same rule the project tree follows: a menu belongs to the state it opens on.
    /// </summary>
    internal static MenuFlyout ResourceMenu(
        SessionInstrumentsViewModel instruments,
        Action<TurnLedgerPageReader> showLedger)
    {
        var flyout = new MenuFlyout();
        var resources = new MenuItem
        {
            Header = InstrumentsStrings.ResourcesCurrent,
            Command = instruments.OpenInspectorCommand,
        };
        AutomationProperties.SetAutomationId(resources, "strip-resources-current");
        flyout.Items.Add(resources);

        if (instruments.SessionLedger is { } read)
        {
            var ledger = new MenuItem { Header = SessionStrings.SessionLedger.Resolve() };
            AutomationProperties.SetAutomationId(ledger, "strip-resources-ledger");
            ledger.Click += (_, _) => showLedger(read);
            flyout.Items.Add(ledger);
        }

        return flyout;
    }

    /// <summary>
    /// Shows the session's timeline over this view's window. The window reads the store itself, a page at a time, so nothing
    /// here has to wait for a read: a page that fails costs that page, not the dialog.
    /// </summary>
    private async Task ShowSessionLedgerAsync(TurnLedgerPageReader read)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await TurnLedgerWindow.ShowAsync(owner, read).ConfigureAwait(true);
        }
    }

    /// <summary>The red strip's click: the same read the prefetch asks for, retried by the reader after a failure.</summary>
    private async void OnLoadOlderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionWorkspaceViewModel model)
        {
            await model.LoadOlderHistoryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The prefetch: reads the page above and keeps the reader's place. The page lands in front of the transcript and both
    /// the panel (it arranges from the first visible round) and the content anchor hold what the reader was looking at, so
    /// there is nothing to scroll back afterwards. Answers whether the behaviour may ask again on a later scroll.
    /// </summary>
    private async Task<bool> LoadOlderAsync()
    {
        if (DataContext is not SessionWorkspaceViewModel model)
        {
            return false;
        }

        var outcome = await model.LoadOlderHistoryAsync(CancellationToken.None);
        return outcome is SessionWorkspaceViewModel.OlderPageLoad.Loaded or SessionWorkspaceViewModel.OlderPageLoad.NotReady;
    }

    /// <summary>
    /// The Chat History flyout is rebuilt from the transcript every time it opens: the transcript is the one list that knows
    /// which rounds exist (a session read backwards from the store grows it a page at a time), so the flyout holds no copy
    /// that could drift from it.
    /// </summary>
    private void OnHistoryOpening(object? sender, EventArgs e)
    {
        if (DataContext is SessionWorkspaceViewModel model)
        {
            model.ComposerHistory.Refresh();
        }
    }

    /// <summary>Clicking a user message writes it back into the composer, after whatever is already typed there.</summary>
    private void OnHistoryFillClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerHistoryEntry entry } && DataContext is SessionWorkspaceViewModel model)
        {
            model.FillFromHistory(entry);
            HideHistoryFlyout(sender);
            this.FindControl<TextBox>("Input")?.Focus();
        }
    }

    /// <summary>The row's "save as instruction": the shell opens the prompts settings with this message prefilled.</summary>
    private void OnHistorySaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerHistoryEntry entry } && DataContext is SessionWorkspaceViewModel model)
        {
            HideHistoryFlyout(sender);
            model.SaveHistoryAsInstruction(entry);
        }
    }

    /// <summary>The row's "locate": the transcript scrolls that message's round to the top and stops following the reply.</summary>
    private void OnHistoryLocateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ComposerHistoryEntry entry })
        {
            HideHistoryFlyout(sender);
            follower?.Locate(entry.RoundIndex);
        }
    }

    /// <summary>
    /// Closes the flyout a row button lives in. The flyout's own object is not in the logical tree of its content (the
    /// presenter's parent is the popup, the popup's is the button that owns it), so the owner is what gets asked.
    /// </summary>
    private static void HideHistoryFlyout(object? sender)
    {
        if (sender is Control control
            && control.GetLogicalAncestors().OfType<Popup>().FirstOrDefault() is { Parent: Button owner })
        {
            owner.Flyout?.Hide();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (items is not null)
        {
            items.CollectionChanged -= OnItemsChanged;
            items = null;
        }

        viewModel = DataContext as SessionWorkspaceViewModel;
        if (viewModel is not null)
        {
            items = viewModel.Transcript.Rounds;
            items.CollectionChanged += OnItemsChanged;
            // An agent-session tab re-shapes the composer's chips - only the kinds that may drive one, the kind's own model,
            // no orchestration chip (forge-app "Agent 会话的界面与接线"); an ordinary session's composer stays as it is.
            viewModel.Composer.IsAgentSession = viewModel.IsAgentSession;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)    {
        base.OnAttachedToVisualTree(e);
        ScrollToEnd();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Scrolling after an add is the transcript follower's job (it stops following once the user scrolls away).
    }

    private void ScrollToEnd()
    {
        var list = this.FindControl<ListBox>("Transcript");
        if (list?.ItemsSource is not { } source)
        {
            return;
        }

        var count = source.Cast<object>().Count();
        if (count > 0)
        {
            list.ScrollIntoView(count - 1);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (viewModel is null)
        {
            return;
        }

        var composer = viewModel.Composer;
        if (composer.IsSuggestionOpen)
        {
            switch (e.Key)
            {
                case Key.Up:
                    composer.MoveSelection(-1);
                    BringSelectedSuggestionIntoView();
                    e.Handled = true;
                    return;
                case Key.Down:
                    composer.MoveSelection(1);
                    BringSelectedSuggestionIntoView();
                    e.Handled = true;
                    return;
                case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                    AcceptSuggestion(null);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    composer.CloseSuggestions(dismissed: true);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        // Idle: Enter and Ctrl+Enter send. Running: Enter interjects, Ctrl+Enter queues, Ctrl+Shift+Enter redirects.
        // Shift+Enter (and Alt+Enter) fall through to the text box, which inserts a line break.
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        System.Windows.Input.ICommand command;
        if (modifiers == KeyModifiers.None)
        {
            command = viewModel.IsRunning ? viewModel.InterjectCommand : viewModel.SendCommand;
        }
        else if (modifiers == KeyModifiers.Control)
        {
            command = viewModel.IsRunning ? viewModel.QueueCommand : viewModel.SendCommand;
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && viewModel.IsRunning)
        {
            command = viewModel.RedirectCommand;
        }
        else
        {
            return;
        }

        e.Handled = true;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnInputPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (viewModel is null || sender is not TextBox input)
        {
            return;
        }

        if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty)
        {
            // The caret settles after the text change, so read both on the next pass.
            Dispatcher.UIThread.Post(() =>
            {
                if (viewModel is { } current && input.IsKeyboardFocusWithin)
                {
                    current.Composer.UpdateSuggestions(input.Text, input.CaretIndex);
                }
            });
        }
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is TextBox { IsKeyboardFocusWithin: false })
            {
                viewModel?.Composer.CloseSuggestions();
            }
        });

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ComposerSuggestionItem item })
        {
            AcceptSuggestion(item);
        }
    }

    private void AcceptSuggestion(ComposerSuggestionItem? item)
    {
        var input = this.FindControl<TextBox>("Input");
        if (viewModel?.Composer.Accept(item) is not { } result || input is null)
        {
            return;
        }

        input.Text = result.Text;
        input.CaretIndex = result.Caret;
        input.Focus();
    }

    private void BringSelectedSuggestionIntoView()
    {
        if (viewModel is null || this.FindControl<ItemsControl>("SuggestionList") is not { } list)
        {
            return;
        }

        var index = viewModel.Composer.SelectedSuggestionIndex;
        if (index >= 0 && list.ContainerFromIndex(index) is { } container)
        {
            container.BringIntoView();
        }
    }
}