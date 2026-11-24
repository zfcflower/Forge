using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Forge.App.Views;

/// <summary>
/// One row of a picker flyout: a hit-testable <see cref="Border"/> that runs a command when it is clicked (or Enter / Space while
/// it has focus) and raises <see cref="Click"/> like a button. It exists because a row is built over and over - every open of a
/// popup attaches its rows to the visual tree afresh, and a templated <c>Button</c> (the button, its presenter and its border, each
/// matched against the whole style sheet) costs roughly an order of magnitude more per row than a decorator does. The
/// bench's <c>composer.model.open</c> row is the number that decides whether that matters.
/// </summary>
internal sealed partial class PickerRow : Border
{

    public static readonly RoutedEvent<RoutedEventArgs> ClickEvent =
        RoutedEvent.Register<PickerRow, RoutedEventArgs>(nameof(Click), RoutingStrategies.Bubble);

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private bool pressed;

    static PickerRow()
    {
        FocusableProperty.OverrideDefaultValue<PickerRow>(true);
    }

    public PickerRow()
    {
        // The row needs a background to be hit-testable, but it is set by the style sheet (Views/ComposerStyles): a local
        // value here would outrank that sheet's `:pointerover` fill and the hover would never paint.
        Cursor = HandCursor;
        Classes.Add("picker-row");
    }

    [GeneratedStyledProperty]
    public partial ICommand? Command { get; set; }

    public event EventHandler<RoutedEventArgs>? Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>Runs the command, then raises <see cref="Click"/> (the handler may close the flyout the row sits in).</summary>
    internal void Activate()
    {
        if (Command is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }

        RaiseEvent(new RoutedEventArgs(ClickEvent, this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            pressed = true;
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed)
        {
            return;
        }

        pressed = false;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            e.Handled = true;
            Activate();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        pressed = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Activate();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PickerRowPeer(this);

    private sealed class PickerRowPeer(PickerRow owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public void Invoke() => owner.Activate();
    }
}
