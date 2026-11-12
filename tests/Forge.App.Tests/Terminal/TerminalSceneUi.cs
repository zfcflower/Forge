using Avalonia.Controls;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;

namespace Forge.App.Tests.Terminal;

/// <summary>A started terminal: the view model the tests drive, the control it renders into and the window around both.</summary>
internal sealed record TerminalScene(TerminalViewModel ViewModel, TerminalControl Control, Window Window);

/// <summary>The terminal-in-a-window plumbing every terminal input suite needs, so each keeps only its own scene record.</summary>
internal static class TerminalSceneUi
{
    /// <summary>Starts a 100x30 terminal on <paramref name="host"/> and shows it in a 1000x700 window.</summary>
    public static TerminalScene Open(FakeTerminalHost host)
    {
        var viewModel = new TerminalViewModel(
            "s1",
            1,
            host,
            new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 30)),
            new AppPreferences());
        var control = new TerminalControl { Source = viewModel };
        var window = new Window { Width = 1000, Height = 700, Content = control };
        viewModel.Start();
        TerminalUi.Show(window);
        return new TerminalScene(viewModel, control, window);
    }
}
