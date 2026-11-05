using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// The three drop targets of a region-swap drag: one translucent box over each docked region, the one under the pointer
/// highlighted. Purely visual — the host owns the drag and does the hit test.
/// </summary>
internal sealed class SwapDropOverlay : Panel
{
    private readonly Border[] boxes = new Border[3];
    private readonly Rect[] targets = new Rect[3];

    public SwapDropOverlay()
    {
        IsHitTestVisible = false;
        for (var i = 0; i < boxes.Length; i++)
        {
            var box = new Border
            {
                IsVisible = false,
                IsHitTestVisible = false,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Opacity = 0.55,
            };
            WorkbenchResources.Link(box, Border.BackgroundProperty, "AccentSubtleBrush");
            WorkbenchResources.Link(box, Border.BorderBrushProperty, "AccentBrush");
            AutomationProperties.SetAutomationId(box, "region-drop-" + (RegionRole)i);
            boxes[i] = box;
        }

        Children.AddRange(boxes);
    }

    /// <summary>Sets the drop rectangles (in host coordinates); <paramref name="active"/> says which roles are docked.</summary>
    public void SetTargets(bool projectActive, Rect project, bool mainActive, Rect main, bool inspectorActive, Rect inspector)
    {
        SetTarget(0, projectActive, project);
        SetTarget(1, mainActive, main);
        SetTarget(2, inspectorActive, inspector);
    }

    /// <summary>Highlights the box the pointer is over, or none when the role is null.</summary>
    public void SetHovered(RegionRole? role)
    {
        var hovered = role is { } value ? (int)value : -1;
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i].Opacity = i == hovered ? 0.95 : 0.55;
            boxes[i].BorderThickness = new Thickness(i == hovered ? 2 : 1);
        }
    }

    /// <summary>Hides every box (the drag ended or never started).</summary>
    public void Clear()
    {
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i].IsVisible = false;
            targets[i] = default;
        }

        IsVisible = false;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < boxes.Length; i++)
        {
            boxes[i].Arrange(targets[i]);
        }

        return finalSize;
    }

    private void SetTarget(int index, bool active, Rect rect)
    {
        targets[index] = active ? rect : default;
        boxes[index].IsVisible = active && rect.Width > 0 && rect.Height > 0;
    }
}
