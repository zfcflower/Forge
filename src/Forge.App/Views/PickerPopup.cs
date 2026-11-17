using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Forge.App.Views;

/// <summary>
/// A chip's picker that stays in the window's overlay layer between uses. A <c>Flyout</c> re-attaches its content to a new popup
/// root on every open, so the styles, templates and row realization run again each time (~40 ms for the model list on a 197-model
/// catalogue); here the content is built and laid out once, and opening is toggling visibility plus a position (~12 ms, close to
/// an empty popup's floor). Placement is above the chip (below when it does not fit), light dismiss is a pointer press outside,
/// and Esc or window deactivation closes it, like the flyout it replaces.
/// </summary>
internal sealed class PickerPopup
{
    private const double Gap = 6;

    private readonly Button chip;
    private readonly Border host;
    private readonly FlyoutPresenter presenter;
    private OverlayLayer? layer;
    private TopLevel? root;
    private TextBox? search;
    private bool warmed;

    public PickerPopup(Button chip, Control content)
    {
        this.chip = chip;
        presenter = new FlyoutPresenter { Content = content };
        KeyboardNavigation.SetTabNavigation(presenter, KeyboardNavigationMode.Cycle);
        host = new Border
        {
            Child = presenter,
            IsVisible = false,
            CornerRadius = new CornerRadius(8),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 16, Color = Color.FromArgb(0x40, 0, 0, 0) }),
        };
        host.SizeChanged += (_, e) =>
        {
            if (IsOpen)
            {
                Reposition(e.NewSize);
            }
        };
        host.KeyDown += OnKeyDown;
        chip.Click += (_, _) => Toggle();
    }

    public bool IsOpen => host.IsVisible;

    /// <summary>The overlay layer is outside the toolbar's tree, so the picker's DataContext is passed along by hand.</summary>
    public void SetDataContext(object? dataContext) => host.DataContext = dataContext;

    public void Toggle()
    {
        if (IsOpen)
        {
            Close(restoreFocus: true);
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (IsOpen || !Attach())
        {
            return;
        }

        host.IsVisible = true;
        host.Measure(Size.Infinity);
        Reposition(host.DesiredSize);

        root = TopLevel.GetTopLevel(chip);
        root?.AddHandler(InputElement.PointerPressedEvent, OnRootPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        if (root is WindowBase window)
        {
            window.Deactivated += OnDeactivated;
        }

        search ??= presenter.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        search?.Focus();
    }

    public void Close(bool restoreFocus)
    {
        if (!IsOpen)
        {
            return;
        }

        host.IsVisible = false;
        root?.RemoveHandler(InputElement.PointerPressedEvent, OnRootPressed);
        if (root is WindowBase window)
        {
            window.Deactivated -= OnDeactivated;
        }

        root = null;
        if (restoreFocus)
        {
            chip.Focus();
        }
    }

    /// <summary>
    /// Builds and lays the content out once, before the first click, without a frame in between so nothing is ever drawn: the
    /// first real open then costs the same as every later one.
    /// </summary>
    public void Warm()
    {
        if (warmed || IsOpen || !Attach())
        {
            return;
        }

        warmed = true;
        host.IsVisible = true;
        host.UpdateLayout();
        host.IsVisible = false;
    }

    /// <summary>Takes the picker out of the overlay layer (the chip left its window); the content is kept for the next attach.</summary>
    public void Detach()
    {
        Close(restoreFocus: false);
        (host.Parent as Panel)?.Children.Remove(host);
        layer = null;
        warmed = false;
    }

    private bool Attach()
    {
        layer ??= OverlayLayer.GetOverlayLayer(chip);
        if (layer is null)
        {
            return false;
        }

        if (host.Parent is null)
        {
            layer.Children.Add(host);
        }

        return true;
    }

    private void Reposition(Size size)
    {
        if (layer is null || chip.TranslatePoint(default, layer) is not { } origin)
        {
            return;
        }

        var x = Math.Clamp(origin.X, 0, Math.Max(0, layer.Bounds.Width - size.Width));
        var y = origin.Y - size.Height - Gap;
        if (y < 0)
        {
            y = Math.Min(origin.Y + chip.Bounds.Height + Gap, Math.Max(0, layer.Bounds.Height - size.Height));
        }

        Canvas.SetLeft(host, x);
        Canvas.SetTop(host, y);
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source
            && (source == host || host.IsVisualAncestorOf(source) || source == chip || chip.IsVisualAncestorOf(source)))
        {
            return;
        }

        Close(restoreFocus: false);
    }

    private void OnDeactivated(object? sender, EventArgs e) => Close(restoreFocus: false);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(restoreFocus: true);
        }
    }
}
