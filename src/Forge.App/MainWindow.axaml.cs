using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Forge.App.Localization;
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
            Dispatcher.UIThread.Post(() => model.Status.SetWorkspaceProblem(ShellStrings.InitializationFailed(exception.Message)));
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
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    public async Task<string?> PromptTextAsync(string title, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 320 };
        var ok = new Button { Content = ShellStrings.Ok, IsDefault = true };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 14,
                Children =
                {
                    input,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok },
                    },
                },
            },
        };

        ok.Click += (_, _) => dialog.Close(input.Text);
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(owner);
    }
}
