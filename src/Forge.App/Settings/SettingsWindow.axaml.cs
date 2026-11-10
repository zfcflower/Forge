using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;

namespace Forge.App.Settings;

/// <summary>
/// The settings window. Code-behind only owns what needs a window: the self-drawn title row (move, close, the section
/// column's fold) and the width-driven auto-fold of that column. Everything else is bound to
/// <see cref="SettingsViewModel"/>.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? page;

    /// <summary>Designer/loader constructor; the window is normally created with its page model.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    public SettingsWindow(SettingsViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        page = viewModel;
        DataContext = viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>Where this column folds by itself; the page model owns the threshold so tests can assert it.</summary>
    internal double AutoCollapseWidth => SettingsViewModel.AutoCollapseWidth;

    private void OnOpened(object? sender, EventArgs e)
    {
        if (page is null)
        {
            return;
        }

        page.IsOpen = true;
        page.CloseRequested -= OnCloseRequested;
        page.CloseRequested += OnCloseRequested;
        page.UpdateAvailableWidth(ClientSize.Width);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (page is null)
        {
            return;
        }

        page.CloseRequested -= OnCloseRequested;
        page.IsOpen = false;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => page?.UpdateAvailableWidth(ClientSize.Width);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) => WindowCaption.Handle(this, e);

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (page is not null && sender is Button { DataContext: SettingsNavItemViewModel item })
        {
            page.SelectedSection = item.Section;
        }
    }

    private void OnToggleNavClick(object? sender, RoutedEventArgs e) => page?.ToggleNav();

    private void OnReloadClick(object? sender, RoutedEventArgs e) => page?.Reload();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
