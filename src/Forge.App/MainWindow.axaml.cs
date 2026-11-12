using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Forge.Workspace;

namespace Forge.App;

/// <summary>
/// The shell window. Code-behind only owns what needs a window: the modal dialogs and the one-time asynchronous
/// initialization (schema migration, layout and tab restore).
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? viewModel;
    private int uiThread;

    public MainWindow()
    {
        InitializeComponent();
        ClampToScreen();
        ApplyWindowStateChrome();
    }

    /// <summary>The maximize button shows what it will do: maximize while normal, restore while maximized.</summary>
    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            ApplyWindowStateChrome();
        }
    }

    private void ApplyWindowStateChrome()
    {
        if (MaximizeButton is null || MaximizeIcon is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        if (this.TryFindResource(maximized ? "IconRestore" : "IconMaximize", out var icon) && icon is Avalonia.Media.Geometry geometry)
        {
            MaximizeIcon.Data = geometry;
        }

        var text = maximized ? ShellStrings.Restore : ShellStrings.Maximize;
        ToolTip.SetTip(MaximizeButton, text);
        Avalonia.Automation.AutomationProperties.SetName(MaximizeButton, text);
    }

    private void ClampToScreen()
    {
        if (Screens.Primary is not { } screen)
        {
            System.Diagnostics.Trace.WriteLine("[shell] no screen available for startup placement");
            return;
        }

        ApplyStartupPlacement(screen.WorkingArea, screen.Scaling, default);
    }

    internal void ApplyStartupPlacement(Avalonia.PixelRect workingArea, double scaling, Avalonia.Size frameInsets)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0 || !double.IsFinite(scaling) || scaling <= 0)
        {
            System.Diagnostics.Trace.WriteLine("[shell] invalid screen geometry for startup placement");
            return;
        }

        var availableWidth = Math.Max(1, (workingArea.Width / scaling) - 16 - frameInsets.Width);
        var availableHeight = Math.Max(1, (workingArea.Height / scaling) - 16 - frameInsets.Height);
        // On small or high-DPI screens, the usual minimum must not force the window off-screen.
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        Position = new Avalonia.PixelPoint(
            workingArea.X + (int)Math.Round((workingArea.Width - ((Width + frameInsets.Width) * scaling)) / 2),
            workingArea.Y + (int)Math.Round((workingArea.Height - ((Height + frameInsets.Height) * scaling)) / 2));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (viewModel is null)
        {
            return;
        }

        var confirmed = closeConfirmed;
        closeConfirmed = false;
        if (!e.Cancel && !confirmed && viewModel.HasUnsavedFiles)
        {
            e.Cancel = true;
            if (!closeConfirming)
            {
                closeConfirming = true;
                _ = ConfirmCloseAsync(viewModel);
            }
        }

        if (e.Cancel)
        {
            // A cancelled close must not leave the layout in "shutting down": floating windows would then be
            // kept floating instead of docking back when they close.
            viewModel.Workbench.IsShuttingDown = false;
            return;
        }

        viewModel.PrepareForShutdown();
    }

    private bool closeConfirmed;
    private bool closeConfirming;

    private async Task ConfirmCloseAsync(MainWindowViewModel model)
    {
        try
        {
            if (await model.ConfirmCloseWindowAsync().ConfigureAwait(true))
            {
                closeConfirmed = true;
                Close();
            }
        }
        catch (InvalidOperationException exception)
        {
            System.Diagnostics.Trace.WriteLine("[shell] close confirmation failed: " + exception.Message);
        }
        finally
        {
            closeConfirming = false;
        }
    }

    /// <summary>The client area extends into the title bar, so the header drags the window and a double click maximizes it.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) => WindowCaption.Handle(this, e);

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowCaption.ToggleMaximize(this);

    protected override void OnOpened(EventArgs e)
    {
        if (WindowState == WindowState.Normal && (Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
        {
            // The actual desktop scale and native frame are only reliable after the platform has opened the window.
            var frame = FrameSize ?? ClientSize;
            var insets = new Avalonia.Size(
                Math.Max(0, frame.Width - ClientSize.Width),
                Math.Max(0, frame.Height - ClientSize.Height));
            ApplyStartupPlacement(screen.WorkingArea, DesktopScaling, insets);
        }

        base.OnOpened(e);
        uiThread = Environment.CurrentManagedThreadId;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        viewModel = DataContext as MainWindowViewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.AttachDialogs(new ShellDialogs(this, viewModel.GitClient));
        _ = InitializeAsync(viewModel);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    /// <summary>The maximize button's tip is owned by the code-behind (it follows the window state), so a language
    /// switch re-applies it here; every other caption text is a binding in the XAML.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A window of a session that is already gone (a test host) may still be subscribed; touching its buttons from
        // another thread would queue a layout pass on the wrong dispatcher.
        if (Environment.CurrentManagedThreadId == uiThread)
        {
            ApplyWindowStateChrome();
        }
    }

    private static async Task InitializeAsync(MainWindowViewModel model)
    {
        try
        {
            await model.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine("[shell] initialization failed: " + exception);
            Dispatcher.UIThread.Post(() =>
            {
                model.Status.SetWorkspaceProblem(ShellStrings.InitializationFailed(exception.Message));
                NotificationCenter.Current?.Notify(
                    NotificationLevel.Error,
                    NotificationsStrings.StartupProblemTitle,
                    ShellStrings.InitializationFailed(exception.Message),
                    "startup-failed");
            });
        }
    }
}

/// <summary>Folder picker plus the small modal windows the shell needs: confirms, text prompts and the new-branch dialog.</summary>
internal sealed class ShellDialogs(Window owner, GitCli git) : IDialogService
{
    /// <summary>
    /// The new-branch dialog: it owns the branch list and the name validation, so the shell only wires the view model to a
    /// window and waits for the answer. Cancelled and closed dialogs both come back as null, and nothing is created.
    /// </summary>
    /// <summary>The pull-request dialog: three inputs and an answer, nothing else; cancelled and closed both come back null.</summary>
    /// <summary>The merge dialog: three strategies, the delete switch, and the answer; cancelled and closed come back null.</summary>
    public async Task<Forge.App.Git.PullRequestMergeChoice?> ComposePullRequestMergeAsync(
        Forge.App.Git.PullRequestMergeRequest request,
        CancellationToken ct)
    {
        var dialog = new MergePullRequestDialog
        {
            DataContext = new MergePullRequestDialogViewModel(request),
        };
        return await dialog.ShowDialog<Forge.App.Git.PullRequestMergeChoice?>(owner).ConfigureAwait(true);
    }

    public async Task<Forge.App.Git.PullRequestDraft?> ComposePullRequestAsync(Forge.App.Git.PullRequestDraft draft, CancellationToken ct)
    {
        var dialog = new PullRequestDialog
        {
            DataContext = new PullRequestDialogViewModel(draft.Title, draft.Body, draft.Draft),
        };
        return await dialog.ShowDialog<Forge.App.Git.PullRequestDraft?>(owner).ConfigureAwait(true);
    }

    public async Task<NewBranchChoice?> PickNewBranchAsync(NewBranchRequest request, CancellationToken ct)
    {
        var model = new NewBranchDialogViewModel(git, request, ct);
        var dialog = new NewBranchDialog { DataContext = model };
        return await dialog.ShowDialog<NewBranchChoice?>(owner).ConfigureAwait(true);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null)
        {
            return null;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        var confirm = new Button { Content = confirmText, IsDefault = true };
        var cancel = new Button { Content = cancelText, IsCancel = true };
        var dialog = CreateDialogWindow(title, 460, new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ButtonRow(cancel, confirm),
            },
        });

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    public async Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options)
    {
        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        var dialog = CreateDialogWindow(title, 480, new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                buttons,
            },
        });

        // The last option is the cancel button; the first is the default.
        for (var i = options.Count - 1; i >= 0; i--)
        {
            var index = i;
            var button = new Button { Content = options[i], IsDefault = i == 0, IsCancel = i == options.Count - 1 };
            button.Click += (_, _) => dialog.Close(index == options.Count - 1 ? -1 : index);
            buttons.Children.Add(button);
        }

        var result = await dialog.ShowDialog<int?>(owner);
        return result ?? -1;
    }

    /// <summary>A list with one choice (7.10): disabled entries stay visible, greyed out, with their reason as a second line.</summary>
    public async Task<int> PickAsync(string title, string message, IReadOnlyList<PickEntry> entries)
    {
        var confirm = new Button { Content = ShellStrings.Ok, IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        var list = new StackPanel { Spacing = 6 };
        var chosen = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            var entry = entries[i];
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = entry.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (entry.DisabledReason is { } reason)
            {
                content.Children.Add(new TextBlock { Text = reason, Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            }

            var radio = new RadioButton { GroupName = "pick", Content = content, IsEnabled = entry.DisabledReason is null };
            Avalonia.Automation.AutomationProperties.SetAutomationId(radio, "pick-entry-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    chosen = index;
                    confirm.IsEnabled = true;
                }
            };
            list.Children.Add(radio);
        }

        var dialog = CreateDialogWindow(title, 560, new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new ScrollViewer { MaxHeight = 360, Content = list },
                ButtonRow(cancel, confirm),
            },
        });

        confirm.Click += (_, _) => dialog.Close(chosen);
        cancel.Click += (_, _) => dialog.Close(-1);
        var result = await dialog.ShowDialog<int?>(owner);
        return result ?? -1;
    }

    /// <summary>
    /// The handover dialog: the commit / carry choice (when the workspace has uncommitted changes) and the context mode
    /// (new session only), then hosted execution - the dialog stays open, shows the error of a failed attempt and lets the
    /// user retry or change the choice. Stop cancels a running attempt (the summary); closing is blocked while it runs.
    /// </summary>
    public async Task<bool?> RunHandoverAsync(Lifecycle.HandoverDialogModel model, CancellationToken ct)
    {
        var body = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = HandoverStrings.Intro(model.OldTitle), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var controls = new List<Control>();
        if (model.HasUncommitted)
        {
            body.Children.Add(new TextBlock { Text = HandoverStrings.Uncommitted(model.UncommittedCount), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var commit = new RadioButton { GroupName = "uncommitted", Content = HandoverStrings.CommitAsOld(model.OldTitle), IsChecked = true };
            var carry = new RadioButton { GroupName = "uncommitted", Content = HandoverStrings.CarryToNew };
            commit.IsCheckedChanged += (_, _) => { if (commit.IsChecked == true) model.Uncommitted = Lifecycle.UncommittedChoice.CommitAsOld; };
            carry.IsCheckedChanged += (_, _) => { if (carry.IsChecked == true) model.Uncommitted = Lifecycle.UncommittedChoice.CarryToNew; };
            body.Children.Add(commit);
            body.Children.Add(carry);
            controls.Add(commit);
            controls.Add(carry);
            if (model.IsRootCheckout)
            {
                body.Children.Add(new TextBlock { Text = HandoverStrings.RootCheckoutWarning, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 });
            }
        }

        if (model.OffersContextMode)
        {
            body.Children.Add(new TextBlock { Text = HandoverStrings.ModeLabel });
            foreach (var (mode, text) in new[]
                     {
                         (Lifecycle.HandoverContextMode.Summary, HandoverStrings.ModeSummary),
                         (Lifecycle.HandoverContextMode.Full, HandoverStrings.ModeFull),
                         (Lifecycle.HandoverContextMode.Blank, HandoverStrings.ModeBlank),
                     })
            {
                var radio = new RadioButton { GroupName = "mode", Content = text, IsChecked = mode == model.Mode };
                radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) model.Mode = mode; };
                body.Children.Add(radio);
                controls.Add(radio);
            }
        }

        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Foreground = Avalonia.Media.Brushes.IndianRed };
        var progress = new TextBlock { Text = HandoverStrings.Running, IsVisible = false };
        var confirm = new Button { Content = HandoverStrings.Title, IsDefault = true };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        body.Children.Add(error);
        body.Children.Add(progress);
        body.Children.Add(ButtonRow(cancel, confirm));
        var dialog = CreateDialogWindow(HandoverStrings.Title, 520, body);

        void Sync()
        {
            var running = model.IsRunning;
            progress.IsVisible = running;
            error.Text = model.Error;
            error.IsVisible = !running && !string.IsNullOrEmpty(model.Error);
            confirm.IsEnabled = !running;
            cancel.Content = running ? HandoverStrings.Stop : ShellStrings.Cancel;
            foreach (var control in controls)
            {
                control.IsEnabled = !running;
            }
        }

        model.PropertyChanged += (_, _) => Sync();
        confirm.Click += async (_, _) =>
        {
            if (await model.ExecuteAsync().ConfigureAwait(true))
            {
                dialog.Close(true);
            }
        };
        cancel.Click += (_, _) =>
        {
            if (model.IsRunning)
            {
                model.CancelRunning();
            }
            else
            {
                dialog.Close(false);
            }
        };
        dialog.Closing += (_, args) =>
        {
            // The switch must not be abandoned half-observed: closing waits for the running attempt (Stop cancels it).
            if (model.IsRunning)
            {
                args.Cancel = true;
                model.CancelRunning();
            }
        };
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (!model.IsRunning)
            {
                dialog.Close(false);
            }
        }));
        Sync();
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true || model.Succeeded;
    }

    /// <summary>
    /// The parallel fork dialog (lifecycle 6.5): the new branch name, the uncommitted-changes choice ("commit first" only
    /// while the source session is idle, else only "fork anyway"), the context mode (only with a source session), the sparse
    /// and cold-build notes, then hosted execution like the handover dialog: it stays open with a failed attempt's error,
    /// Stop cancels a running attempt and closing waits for it.
    /// </summary>
    public async Task<bool?> RunForkAsync(Lifecycle.ForkDialogModel model, CancellationToken ct)
    {
        var body = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = ForkStrings.Intro(model.SourceBranch, model.StartCommit), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var controls = new List<Control>();
        body.Children.Add(new TextBlock { Text = ForkStrings.BranchLabel });
        var branch = new TextBox { Text = model.BranchName };
        branch.TextChanged += (_, _) => model.BranchName = branch.Text ?? string.Empty;
        body.Children.Add(branch);
        controls.Add(branch);
        if (model.HasUncommitted)
        {
            body.Children.Add(new TextBlock { Text = ForkStrings.Uncommitted(model.UncommittedCount), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (model.OffersCommitFirst)
            {
                var commit = new RadioButton { GroupName = "fork-uncommitted", Content = ForkStrings.CommitFirst(model.SourceTitle!), IsChecked = model.CommitFirst };
                var anyway = new RadioButton { GroupName = "fork-uncommitted", Content = ForkStrings.ForkAnyway, IsChecked = !model.CommitFirst };
                commit.IsCheckedChanged += (_, _) => { if (commit.IsChecked == true) model.CommitFirst = true; };
                anyway.IsCheckedChanged += (_, _) => { if (anyway.IsChecked == true) model.CommitFirst = false; };
                body.Children.Add(commit);
                body.Children.Add(anyway);
                controls.Add(commit);
                controls.Add(anyway);
            }
            else
            {
                if (model.SourceBusy && model.SourceTitle is { } busyTitle)
                {
                    body.Children.Add(new TextBlock { Text = ForkStrings.SourceBusy(busyTitle), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                }

                body.Children.Add(new RadioButton { GroupName = "fork-uncommitted", Content = ForkStrings.ForkAnyway, IsChecked = true, IsEnabled = false });
            }

            body.Children.Add(new TextBlock { Text = ForkStrings.NotCarried(model.UncommittedCount), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 });
        }

        if (model.OffersContextMode)
        {
            body.Children.Add(new TextBlock { Text = HandoverStrings.ModeLabel });
            foreach (var (mode, text) in new[]
                     {
                         (Lifecycle.HandoverContextMode.Summary, HandoverStrings.ModeSummary),
                         (Lifecycle.HandoverContextMode.Full, HandoverStrings.ModeFull),
                         (Lifecycle.HandoverContextMode.Blank, HandoverStrings.ModeBlank),
                     })
            {
                var radio = new RadioButton { GroupName = "fork-mode", Content = text, IsChecked = mode == model.Mode };
                radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) model.Mode = mode; };
                body.Children.Add(radio);
                controls.Add(radio);
            }
        }
        else
        {
            body.Children.Add(new TextBlock { Text = ForkStrings.NoSession, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        if (model.IsSparse)
        {
            body.Children.Add(new TextBlock { Text = ForkStrings.Sparse, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 });
        }

        body.Children.Add(new TextBlock { Text = ForkStrings.ColdBuild, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8 });
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, Foreground = Avalonia.Media.Brushes.IndianRed };
        var progress = new TextBlock { Text = ForkStrings.Running, IsVisible = false };
        var confirm = new Button { Content = ForkStrings.Confirm, IsDefault = true };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        body.Children.Add(error);
        body.Children.Add(progress);
        body.Children.Add(ButtonRow(cancel, confirm));
        var dialog = CreateDialogWindow(ForkStrings.Title, 560, body);

        void Sync()
        {
            var running = model.IsRunning;
            progress.IsVisible = running;
            error.Text = model.Error;
            error.IsVisible = !running && !string.IsNullOrEmpty(model.Error);
            confirm.IsEnabled = !running;
            cancel.Content = running ? HandoverStrings.Stop : ShellStrings.Cancel;
            foreach (var control in controls)
            {
                control.IsEnabled = !running;
            }
        }

        model.PropertyChanged += (_, _) => Sync();
        confirm.Click += async (_, _) =>
        {
            if (await model.ExecuteAsync().ConfigureAwait(true))
            {
                dialog.Close(true);
            }
        };
        cancel.Click += (_, _) =>
        {
            if (model.IsRunning)
            {
                model.CancelRunning();
            }
            else
            {
                dialog.Close(false);
            }
        };
        dialog.Closing += (_, args) =>
        {
            if (model.IsRunning)
            {
                args.Cancel = true;
                model.CancelRunning();
            }
        };
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (!model.IsRunning)
            {
                dialog.Close(false);
            }
        }));
        Sync();
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true || model.Succeeded;
    }

    public async Task<string?> PromptTextAsync(string title, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = ShellStrings.Ok, IsDefault = true };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        var dialog = CreateDialogWindow(title, 460, new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 14,
            Children =
            {
                input,
                ButtonRow(cancel, ok),
            },
        });

        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(owner);
    }

    /// <summary>The modal dialog frame every prompt in this window uses: a title, a fixed width, and content-sized height.</summary>
    private static Window CreateDialogWindow(string title, double width, Control content) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = content,
    };

    /// <summary>The footer row of a dialog: cancel on the left, the default action on the right.</summary>
    private static StackPanel ButtonRow(Control cancel, Control confirm)
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        return row;
    }
}
