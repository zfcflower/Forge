using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.ViewModels;

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

    /// <summary>A window larger than the screen is clamped by the platform but laid out at the requested size.</summary>
    private void ClampToScreen()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var availableWidth = (screen.WorkingArea.Width / scaling) - 16;
        var availableHeight = (screen.WorkingArea.Height / scaling) - 16;
        if (Width > availableWidth)
        {
            Width = Math.Max(MinWidth, availableWidth);
        }

        if (Height > availableHeight)
        {
            Height = Math.Max(MinHeight, availableHeight);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (viewModel is null)
        {
            return;
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

    /// <summary>The client area extends into the title bar, so the header drags the window and a double click maximizes it.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && (source is Button or TextBox || source.FindAncestorOfType<Button>() is not null))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        uiThread = Environment.CurrentManagedThreadId;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        viewModel = DataContext as MainWindowViewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.AttachDialogs(new ShellDialogs(this));
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

/// <summary>Folder picker plus the two small modal windows the shell needs.</summary>
internal sealed class ShellDialogs(Window owner) : IDialogService
{
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
