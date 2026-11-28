using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace Forge.App.Views;

/// <summary>
/// Where a tooltip opens: centred on the control that owns it, one gap below it, growing to both sides. When the window leaves
/// no room below, the tip goes above the control instead - still centred. Only a tip that fits neither above nor below leaves
/// the vertical axis, and then it grows out of the control's side: rightwards from the control's right edge, or leftwards from
/// its left edge, vertically centred on it.
/// <para>Avalonia's default anchors the tip to the <em>pointer</em> with a 20 px drop, so a tip opened near a window edge -
/// the composer bar, a tree's trailing buttons, a title row - is slid by the positioner until it fits, and the tip then sits
/// a whole tip-width away from the cursor. Anchoring to the control keeps that distance constant: changing sides changes where
/// the tip is relative to the window, never how far it is from the thing it explains.</para>
/// </summary>
internal static class ToolTipPlacement
{
    /// <summary>The distance between the control and its tip; one step of the 4 px rhythm.</summary>
    internal const double Gap = 6;

    /// <summary>
    /// Set on a control whose tip must follow the pointer instead (the editor's hover card, which answers about the word under
    /// the cursor rather than about the editor).
    /// </summary>
    private static readonly AttachedProperty<bool> FollowsPointerProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FollowsPointer", typeof(ToolTipPlacement));

    private static bool installed;

    /// <summary>
    /// Places one tip: below when it fits (centred, so it grows to both sides), else above (still centred), else out of whichever
    /// side has more room - there it grows away from the control and stays vertically centred on it.
    /// </summary>
    private static readonly CustomPopupPlacementCallback Place = static placement =>
    {
        var size = placement.PopupSize;
        var anchor = placement.AnchorRectangle;
        var window = TopLevel.GetTopLevel(placement.Target)?.ClientSize ?? default;

        if (window.Height - anchor.Bottom >= size.Height + Gap)
        {
            // Under the control, centred on it: the tip's own width grows to the left and to the right alike.
            placement.Anchor = PopupAnchor.Bottom;
            placement.Gravity = PopupGravity.Bottom;
            placement.Offset = new Point(0, Gap);
        }
        else if (anchor.Top >= size.Height + Gap)
        {
            placement.Anchor = PopupAnchor.Top;
            placement.Gravity = PopupGravity.Top;
            placement.Offset = new Point(0, -Gap);
        }
        else if (window.Width - anchor.Right >= anchor.Left)
        {
            // Too tall for either side of the vertical: it leaves through the right edge and grows rightwards, vertically
            // centred on the control (no Top/Bottom flag on either the anchor or the gravity).
            placement.Anchor = PopupAnchor.Right;
            placement.Gravity = PopupGravity.Right;
            placement.Offset = new Point(Gap, 0);
        }
        else
        {
            placement.Anchor = PopupAnchor.Left;
            placement.Gravity = PopupGravity.Left;
            placement.Offset = new Point(-Gap, 0);
        }

        // The side above was chosen here, so the positioner only gets to slide and resize: a flip would undo the choice.
        placement.ConstraintAdjustment =
            PopupPositionerConstraintAdjustment.SlideX |
            PopupPositionerConstraintAdjustment.SlideY |
            PopupPositionerConstraintAdjustment.ResizeX |
            PopupPositionerConstraintAdjustment.ResizeY;
    };

    /// <summary>Hooks every tooltip once per process; called while the application initializes.</summary>
    public static void Install()
    {
        if (installed)
        {
            return;
        }

        installed = true;
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(static (control, _) => Anchor(control));
    }

    /// <summary>Keeps this control's tooltip at the pointer instead of anchoring it to the control.</summary>
    internal static void FollowPointer(Control control) => control.SetValue(FollowsPointerProperty, true);

    private static void Anchor(Control control)
    {
        if (control.GetValue(FollowsPointerProperty))
        {
            return;
        }

        ToolTip.SetPlacement(control, PlacementMode.Custom);
        ToolTip.SetCustomPopupPlacementCallback(control, Place);
    }
}
