using Avalonia.Controls;

namespace Forge.App.Terminal;

/// <summary>
/// The body of a terminal tab: the close banner, the surface and the status line. The view holds no state of its own;
/// everything it shows is a property of <see cref="TerminalViewModel"/>, so a floating window that renders the same view
/// model shows the same terminal.
/// </summary>
public partial class TerminalView : UserControl
{
    public TerminalView() => InitializeComponent();
}
