using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>One tool call row: header, arguments and result. Frozen; waves W4 and W8 edit their own child views.</summary>
public partial class ToolRowView : UserControl
{
    public ToolRowView()
    {
        InitializeComponent();
        // The open part of the row is built from a template when the row opens (see ToolRowViewModel.ExpandedBody), and a
        // template cannot name a handler of this class: its "open the file" button is recognised here, as its click bubbles up.
        AddHandler(Button.ClickEvent, OnBodyClick);
    }

    private void OnBodyClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button button && Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "tool-open-file")
        {
            OnOpenTarget(button, e);
        }
    }

    /// <summary>Opens the file the call worked on: source in the editor, PDF/HTML/pictures in the system's application.</summary>
    private void OnOpenTarget(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ToolRowViewModel { TargetPath: { } path })
        {
            _ = LinkLauncher.OpenAsync(this, path);
        }
    }
}
