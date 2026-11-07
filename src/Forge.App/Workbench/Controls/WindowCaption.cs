using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Forge.App.Workbench.Controls;

/// <summary>What a press on a self-drawn caption area does to its window.</summary>
internal enum CaptionPress
{
    /// <summary>The press belongs to the content, not to the window.</summary>
    None,

    /// <summary>The press starts a window move.</summary>
    Move,

    /// <summary>A double press maximizes a normal window and restores a maximized one.</summary>
    ToggleMaximize,
}

/// <summary>
/// The rule behind every self-drawn caption. Both windows drop the system title bar
/// (<c>WindowDecorations.BorderOnly</c>), so the OS never moves them and the app's own title row has to. It is written
/// once here for <see cref="MainWindow"/> and <see cref="FloatingPanelWindow"/>, so the exclusion of buttons and text
/// boxes cannot drift apart between them.
/// </summary>
internal static class WindowCaption
{
    /// <summary>
    /// Starts the window move. A test seam: the headless platform does not move windows, so replacing this delegate is
    /// how a test observes that a press started one.
    /// </summary>
    internal static Action<Window, PointerPressedEventArgs> BeginMove { get; set; } =
        static (window, e) => window.BeginMoveDrag(e);

    /// <summary>Classifies one press on a caption area.</summary>
    /// <param name="leftButtonPressed">Whether the left button is down.</param>
    /// <param name="clickCount">The platform click count of the press; a triple click is a move, as it always was.</param>
    /// <param name="interactive">True when the press landed on a control that owns its own clicks.</param>
    internal static CaptionPress Classify(bool leftButtonPressed, int clickCount, bool interactive) =>
        !leftButtonPressed || interactive
            ? CaptionPress.None
            : clickCount == 2
                ? CaptionPress.ToggleMaximize
                : CaptionPress.Move;

    /// <summary>Handles one press on a caption area.</summary>
    internal static void Handle(Window window, PointerPressedEventArgs e)
    {
        var press = Classify(
            e.GetCurrentPoint(window).Properties.IsLeftButtonPressed,
            e.ClickCount,
            IsInteractive(e.Source));
        switch (press)
        {
            case CaptionPress.Move:
                BeginMove(window, e);
                break;
            case CaptionPress.ToggleMaximize:
                ToggleMaximize(window);
                break;
        }
    }

    /// <summary>Maximizes a normal window and restores a maximized one.</summary>
    internal static void ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>A button, a text box, or anything inside a button keeps its own press.</summary>
    private static bool IsInteractive(object? source) =>
        source is Control control &&
        (control is Button or TextBox || control.FindAncestorOfType<Button>() is not null);
}
