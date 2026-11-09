using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Localization;

namespace Forge.App.Changes;

internal sealed partial class DiffFileHeader : UserControl
{
    private IDiffFileItem? item;

    public DiffFileHeader()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (item is not null)
        {
            item.PropertyChanged -= OnItemChanged;
        }

        item = DataContext as IDiffFileItem;
        if (item is not null)
        {
            item.PropertyChanged += OnItemChanged;
        }

        UpdateCounts();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(IDiffFileItem.Added) or nameof(IDiffFileItem.Removed))
        {
            UpdateCounts();
        }
    }

    private void UpdateCounts()
    {
        AddedText.Text = item is null ? null : "+" + item.Added.ToString(CultureInfo.InvariantCulture);
        RemovedText.Text = item is null ? null : "−" + item.Removed.ToString(CultureInfo.InvariantCulture);
    }

    private void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (item is not null)
        {
            item.IsMarkdownPreview = true;
        }
    }

    private void OnSourceClick(object? sender, RoutedEventArgs e)
    {
        if (item is not null)
        {
            item.IsMarkdownPreview = false;
        }
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (item is null || sender is not Button button)
        {
            return;
        }

        var loc = Loc.Instance;
        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem
        {
            Header = loc[DiffStrings.CopyPath],
            Command = item.CopyPathCommand,
        });
        flyout.Items.Add(new MenuItem
        {
            Header = loc[DiffStrings.OpenInEditor],
            Command = item.OpenInEditorCommand,
        });
        flyout.ShowAt(button);
    }
}

/// <summary>
/// A horizontal row with one flexible child (the path) and one trailing cluster. Everything else takes its natural width; the
/// flexible child gets what is left after the trailing cluster, so the path shortens before any button is pushed out.
/// </summary>
internal sealed class FlexRowPanel : Panel
{
    public static readonly AttachedProperty<bool> IsFlexibleProperty =
        AvaloniaProperty.RegisterAttached<FlexRowPanel, Control, bool>("IsFlexible");

    public static readonly AttachedProperty<bool> IsTrailingProperty =
        AvaloniaProperty.RegisterAttached<FlexRowPanel, Control, bool>("IsTrailing");

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlexRowPanel, double>(nameof(Spacing));

    static FlexRowPanel()
    {
        AffectsMeasure<FlexRowPanel>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static bool GetIsFlexible(Control control) => control.GetValue(IsFlexibleProperty);

    public static void SetIsFlexible(Control control, bool value) => control.SetValue(IsFlexibleProperty, value);

    public static bool GetIsTrailing(Control control) => control.GetValue(IsTrailingProperty);

    public static void SetIsTrailing(Control control, bool value) => control.SetValue(IsTrailingProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double fixedWidth = 0, height = 0;
        var visible = 0;
        Control? flex = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            visible++;
            if (GetIsFlexible(child))
            {
                flex = child;
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            fixedWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (flex is not null)
        {
            var room = double.IsInfinity(availableSize.Width)
                ? double.PositiveInfinity
                : Math.Max(0, availableSize.Width - fixedWidth - Math.Max(0, visible - 1) * Spacing);
            flex.Measure(new Size(room, availableSize.Height));
            fixedWidth += flex.DesiredSize.Width;
            height = Math.Max(height, flex.DesiredSize.Height);
        }

        return new Size(fixedWidth + Math.Max(0, visible - 1) * Spacing, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        Control? trailing = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (GetIsTrailing(child))
            {
                trailing = child;
                continue;
            }

            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        if (trailing is not null)
        {
            var width = trailing.DesiredSize.Width;
            trailing.Arrange(new Rect(Math.Max(x, finalSize.Width - width), 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
