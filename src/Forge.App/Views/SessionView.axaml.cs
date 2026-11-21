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
            AttachImageInput(input);
        }

        if (this.FindControl<ListBox>("Transcript") is { } transcript)
        {
            TranscriptFollower.Attach(transcript);
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