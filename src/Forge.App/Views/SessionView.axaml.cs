using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class SessionView : UserControl
{
    private SessionWorkspaceViewModel? viewModel;
    private INotifyCollectionChanged? items;

    public SessionView()
    {
        InitializeComponent();
        var input = this.FindControl<TextBox>("Input");
        if (input is not null)
        {
            // Tunnel, so Enter, Tab and the arrows reach the pickers before the text box consumes them.
            input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            input.PropertyChanged += OnInputPropertyChanged;
            input.LostFocus += OnInputLostFocus;
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
            items = viewModel.Transcript.Items;
            items.CollectionChanged += OnItemsChanged;
        }
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)    {
        base.OnAttachedToVisualTree(e);
        ScrollToEnd();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
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

        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        // Ctrl+Enter sends; while a turn runs it queues the message instead.
        e.Handled = true;
        var command = viewModel.IsRunning ? (System.Windows.Input.ICommand)viewModel.QueueCommand : viewModel.SendCommand;
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

    /// <summary>
    /// The strip gives up information as the card narrows: the ahead-behind/CI group goes first. The resources button, the
    /// sub-agent toggle, the workspace button and the token readout stay — the threshold lives in
    /// <see cref="InstrumentLayout"/>, so the priority is testable without a visual tree; this handler only applies the flags
    /// to the named pieces of the strip.
    /// </summary>
    private void OnInstrumentStripSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var layout = InstrumentLayout.For(e.NewSize.Width);
        if (this.FindControl<StackPanel>("StripGit") is { } git)
        {
            git.IsVisible = layout.ShowGit;
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

    private void OnAgentPicked(object? sender, RoutedEventArgs e) => this.FindControl<Button>("AgentChip")?.Flyout?.Hide();

    private void OnModelPicked(object? sender, RoutedEventArgs e) => this.FindControl<Button>("ModelChip")?.Flyout?.Hide();
}