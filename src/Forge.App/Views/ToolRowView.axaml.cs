using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>One tool call row: header, arguments and result. Frozen; waves W4 and W8 edit their own child views.</summary>
public partial class ToolRowView : UserControl
{
    public ToolRowView() => InitializeComponent();

    /// <summary>Opens the file the call worked on: source in the editor, PDF/HTML/pictures in the system's application.</summary>
    private void OnOpenTarget(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ToolRowViewModel { TargetPath: { } path })
        {
            _ = LinkLauncher.OpenAsync(this, path);
        }
    }
}
