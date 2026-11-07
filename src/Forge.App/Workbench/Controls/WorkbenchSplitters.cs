using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;

namespace Forge.App.Workbench.Controls;

/// <summary>
/// Base of the two splitter kinds: a 6 px hit band drawn as a thin line. The drag is reported as the total pointer travel
/// since the press, so the model can always recompute from its start widths.
/// </summary>
internal abstract class WorkbenchSplitter : TemplatedControl
{
    private static readonly Cursor HorizontalDragCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalDragCursor = new(StandardCursorType.SizeNorthSouth);

    private bool dragging;
    private double start;

    protected WorkbenchSplitter()
    {
        IsTabStop = false;
        Cursor = IsVerticalLine ? HorizontalDragCursor : VerticalDragCursor;
    }

    /// <summary>True when the line runs vertically (so the pointer travels horizontally).</summary>
    protected abstract bool IsVerticalLine { get; }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        dragging = true;
        start = Axis(e.GetPosition(TopLevel.GetTopLevel(this) as Visual ?? this));
        PseudoClasses.Set(":dragging", true);
        e.Pointer.Capture(this);
        e.Handled = true;
        OnBeginDrag();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!dragging)
        {
            return;
        }

        OnUpdateDrag(Axis(e.GetPosition(TopLevel.GetTopLevel(this) as Visual ?? this)) - start);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging)
        {
            return;
        }

        dragging = false;
        e.Pointer.Capture(null);
        PseudoClasses.Set(":dragging", false);
        e.Handled = true;
        OnEndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!dragging)
        {
            return;
        }

        dragging = false;
        PseudoClasses.Set(":dragging", false);
        OnEndDrag();
    }

    protected abstract void OnBeginDrag();

    protected abstract void OnUpdateDrag(double delta);

    protected abstract void OnEndDrag();

    private double Axis(Point point) => IsVerticalLine ? point.X : point.Y;
}

/// <summary>The 2 px splitter between two docked regions; the drag goes through the layout's width solver.</summary>
internal sealed class RegionSplitter : WorkbenchSplitter
{
    private int splitterIndex;

    /// <summary>Index of the gap in the sequence of docked regions, left to right.</summary>
    public int SplitterIndex
    {
        get => splitterIndex;
        set
        {
            splitterIndex = value;
            AutomationProperties.SetAutomationId(this, "region-splitter-" + value);
        }
    }

    public WorkbenchLayout? Layout { get; set; }

    protected override bool IsVerticalLine => true;

    protected override void OnBeginDrag() => Layout?.BeginSplitterDrag(SplitterIndex);

    protected override void OnUpdateDrag(double delta) => Layout?.UpdateSplitterDrag(delta);

    protected override void OnEndDrag() => Layout?.EndSplitterDrag();
}

/// <summary>Which panel gap of a region a <see cref="PanelSplitter"/> sits in.</summary>
internal enum PanelSplitterKind
{
    Bottom,
    Preview,
    Inspector,
}

/// <summary>The 1 px splitter between two panels of one region; dragging writes the split back to the layout.</summary>
internal sealed class PanelSplitter : WorkbenchSplitter
{
    public static readonly StyledProperty<bool> IsVerticalProperty =
        AvaloniaProperty.Register<PanelSplitter, bool>(nameof(IsVertical));

    private PanelSplitterKind kind;
    private double startValue;

    public WorkbenchLayout? Layout { get; set; }

    public PanelSplitterKind Kind
    {
        get => kind;
        set
        {
            kind = value;
            SetCurrentValue(IsVerticalProperty, value == PanelSplitterKind.Preview);
        }
    }

    /// <summary>Length of the axis the split moves along: the region height, or the column width for the preview gap.</summary>
    public double TrackLength { get; set; }

    /// <summary>True for the line between the preview column and the files column.</summary>
    public bool IsVertical
    {
        get => GetValue(IsVerticalProperty);
        set => SetValue(IsVerticalProperty, value);
    }

    protected override bool IsVerticalLine => IsVertical;


    protected override void OnBeginDrag()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        startValue = kind switch
        {
            PanelSplitterKind.Bottom => layout.BottomHeight,
            PanelSplitterKind.Preview => layout.PreviewWidth,
            _ => layout.InspectorSplitRatio,
        };
    }

    protected override void OnUpdateDrag(double delta)
    {
        if (Layout is not { } layout)
        {
            return;
        }

        switch (kind)
        {
            case PanelSplitterKind.Bottom:
            {
                var maximum = Math.Max(WorkbenchMetrics.PanelMinHeight, TrackLength - WorkbenchMetrics.PanelMinHeight);
                layout.BottomHeight = Math.Clamp(startValue - delta, WorkbenchMetrics.PanelMinHeight, maximum);
                break;
            }

            case PanelSplitterKind.Preview:
            {
                var direction = layout.IsPreviewLeftOfFiles ? 1 : -1;
                var maximum = Math.Max(WorkbenchMetrics.PreviewMinWidth, TrackLength - WorkbenchMetrics.SideMinWidth);
                layout.PreviewWidth = Math.Clamp(
                    startValue + (delta * direction),
                    WorkbenchMetrics.PreviewMinWidth,
                    maximum);
                break;
            }

            default:
            {
                if (TrackLength <= 2 * WorkbenchMetrics.PanelMinHeight)
                {
                    return;
                }

                var minimum = WorkbenchMetrics.PanelMinHeight / TrackLength;
                layout.InspectorSplitRatio = Math.Clamp(startValue + (delta / TrackLength), minimum, 1 - minimum);
                break;
            }
        }
    }

    /// <summary>Ends the drag by telling the layout to snapshot: the final width/ratio already went through its setters.</summary>
    protected override void OnEndDrag() => Layout?.EndPanelSplitterDrag();
}
