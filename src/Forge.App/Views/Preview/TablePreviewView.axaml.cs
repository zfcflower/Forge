using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HAlign = Avalonia.Layout.HorizontalAlignment;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Views.Preview;

/// <summary>
/// The table preview: a <see cref="TableView"/> over the parsed rows. The column count comes from the file, so the columns
/// are built here rather than in XAML; the control itself owns the header band, the scroll bars, the column layout, the row
/// virtualization and the separators the user drags to size a column. Column widths start as content widths in pixels,
/// scaled up to fill the view while the table still fits.
/// </summary>
public partial class TablePreviewView : UserControl
{
    /// <summary>Room the columns may leave unused before they are spread over the view instead of kept at content width.</summary>
    private const double StarSlack = 1;

    /// <summary>
    /// The floor under a column once the user drags its separator. It is not applied to the columns as they are built: the
    /// fill pass must keep the exact content widths, or a table of many narrow columns would be wider than the view and grow
    /// a scrollbar it was never meant to have.
    /// </summary>
    private const double MinColumnWidth = 40;

    private readonly IDataTemplate? cellTemplate;
    private readonly List<TableViewColumn> watched = [];

    /// <summary>The control's scroll viewer: its viewport is the width the columns really get. Replaced with the template.</summary>
    private ScrollViewer? scroller;
    private bool widthsPending;

    /// <summary>
    /// Set once the user drags a separator: the fill-to-view pass stops, because the next size change would otherwise undo
    /// the width the user just chose. Reset when the columns are rebuilt for another file.
    /// </summary>
    private bool widthsLocked;

    /// <summary>True while <see cref="ApplyWidths"/> writes widths, so its own writes are not taken for the user's.</summary>
    private bool applyingWidths;

    public TablePreviewView()
    {
        InitializeComponent();
        cellTemplate = ResolveCellTemplate();
        Table.TemplateApplied += (_, _) =>
        {
            // The viewport belongs to the control's template: a re-applied template replaces it.
            scroller = null;
            RequestWidths();
        };
        Table.ContainerPrepared += OnContainerPrepared;
        // The widths are applied from the table's own size, on the dispatcher: writing a column width from inside a layout
        // pass leaves the header arranged with the previous widths while the rows already have the new ones.
        Table.SizeChanged += (_, _) => RequestWidths();
        DataContextChanged += (_, _) => BuildColumns();
    }

    /// <summary>One pending width pass at a time; it runs after the current layout pass, not during it.</summary>
    private void RequestWidths()
    {
        if (widthsPending || widthsLocked)
        {
            return;
        }

        widthsPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                widthsPending = false;
                ApplyWidths();
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>Band the rows: the container is re-prepared for every item, so a recycled row never keeps a stale class.</summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => e.Container.Classes.Set("alt", e.Index % 2 == 1);

    private IDataTemplate? ResolveCellTemplate()
        => this.TryFindResource("TableCellTemplate", out var resource) ? resource as IDataTemplate : null;

    private void BuildColumns()
    {
        UnwatchColumns();
        widthsLocked = false;

        if (DataContext is not TableFilePreviewViewModel table)
        {
            Table.Columns = new AvaloniaList<TableViewColumn>();
            return;
        }

        // One swap instead of one add per column: the control rebuilds its headers and every realized row on each change,
        // which a 1024-column CSV would otherwise pay for a thousand times.
        var columns = new AvaloniaList<TableViewColumn>();
        var header = table.Header;
        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = index;
            // A file whose first row was not recognized as a header (the reader only claims one over a numeric column) still
            // gets the band, because the separators that resize a column live in it: it shows spreadsheet column letters.
            var title = header is not null && column < header.Cells.Count ? header.Cells[column].Text : ColumnLetters(column);
            columns.Add(new TableViewColumn
            {
                Header = title,
                Width = new GridLength(table.Columns[column].BaseWidth),
                CellTemplate = BuildCellTemplate(column),
                // The cell draws its own grid line. Left is the cell's default content alignment, which sizes that line to the
                // text instead of to the column, so the header band and the rows disagree on where the columns end.
                HorizontalContentAlignment = HAlign.Stretch,
            });
        }

        Table.Columns = columns;
        WatchColumns(columns);
        RequestWidths();
    }

    /// <summary>
    /// One column's cells. The shared template draws one cell from its view model, so the visual markup stays in XAML while
    /// only the column index (which a table's shape supplies at run time) is wired up here.
    /// </summary>
    private IDataTemplate BuildCellTemplate(int column)
        => new FuncDataTemplate<TablePreviewRowViewModel>((row, _) => BuildCell(row, column));

    /// <summary>
    /// Builds one cell. The row can be null here: swapping the columns rebuilds the cells of every realized row, and a row
    /// whose items source was just cleared still asks its template for content.
    /// </summary>
    private Control BuildCell(TablePreviewRowViewModel? row, int column)
    {
        if (row is null || column >= row.Cells.Count)
        {
            return new TextBlock();
        }

        var cell = row.Cells[column];
        if (cellTemplate is null)
        {
            return new TextBlock { Text = cell.Text };
        }

        // Build takes a data context; a template built by hand does not get one from a presenter.
        var control = cellTemplate.Build(cell) ?? new TextBlock();
        control.DataContext = cell;
        return control;
    }

    /// <summary>
    /// The columns are always sized in pixels — the content width, or the same widths scaled up to fill the view. Stars would
    /// leave the cell widths to whichever presenter measures first, and the header and the rows can then disagree.
    /// <para>The fill pass is a first impression only: a width the user dragged outranks it, so it stops for good at the first
    /// one.</para>
    /// </summary>
    private void ApplyWidths()
    {
        if (widthsLocked || DataContext is not TableFilePreviewViewModel table || table.Columns.Count != Table.Columns.Count)
        {
            return;
        }

        var available = ViewportWidth();
        if (!(available > 0))
        {
            return;
        }

        var content = 0.0;
        foreach (var column in table.Columns)
        {
            content += column.BaseWidth;
        }

        if (!(content > 0))
        {
            return;
        }

        // Rounded on the cumulative edge, in device pixels: the control snaps every column to whole device pixels, so rounding
        // in device-independent pixels leaves the last column a pixel short of the frame (or a pixel past it, which grows a
        // scrollbar) as soon as the display is scaled.
        var fill = content < available - StarSlack;
        var scale = fill ? available / content : 1;
        var dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var totalPx = Math.Floor(available * dpi);
        var edge = 0.0;
        var drawnPx = 0.0;
        applyingWidths = true;
        try
        {
            for (var index = 0; index < table.Columns.Count; index++)
            {
                edge += table.Columns[index].BaseWidth;

                // Only a table being spread over the view lets its last column take the remainder; one that is wider than the
                // view keeps every content width and scrolls, so the remainder would clip it to the viewport.
                var rightPx = fill && index == table.Columns.Count - 1 ? totalPx : Math.Floor(edge * scale * dpi);
                var width = new GridLength(Math.Max(rightPx - drawnPx, 0) / dpi);
                drawnPx = rightPx;
                if (!Table.Columns[index].Width.Equals(width))
                {
                    Table.Columns[index].Width = width;
                }
            }
        }
        finally
        {
            applyingWidths = false;
        }
    }

    /// <summary>
    /// The width the columns are laid out against: the scroll viewer's viewport, which already excludes the frame the control
    /// paints inside its own bounds. Falls back to the bounds (minus that frame) while the template is still being applied.
    /// </summary>
    private double ViewportWidth()
    {
        scroller ??= Table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        var viewport = scroller?.Viewport.Width ?? 0;
        return viewport > 0
            ? viewport
            : Table.Bounds.Width - Table.BorderThickness.Left - Table.BorderThickness.Right;
    }

    /// <summary>
    /// Watches every column's width. The control turns a dragged column into a pixel width, and that write — the only one
    /// this view does not make itself — is what locks the fill pass and puts the resize floor under that column.
    /// </summary>
    private void WatchColumns(AvaloniaList<TableViewColumn> columns)
    {
        foreach (var column in columns)
        {
            column.PropertyChanged += OnColumnPropertyChanged;
            watched.Add(column);
        }
    }

    private void UnwatchColumns()
    {
        foreach (var column in watched)
        {
            column.PropertyChanged -= OnColumnPropertyChanged;
        }

        watched.Clear();
    }

    private void OnColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!applyingWidths && e.Property == TableViewColumn.WidthProperty)
        {
            widthsLocked = true;

            // Only the column the user has in hand gets the floor: the rest keep their content widths, which is what the fill
            // pass and the "keep the content width and scroll" rule are written against.
            if (sender is TableViewColumn column)
            {
                column.MinWidth = MinColumnWidth;
            }
        }
    }

    /// <summary>The spreadsheet label for a column: A…Z, then AA, AB… A table with no header row still names its columns.</summary>
    private static string ColumnLetters(int index)
    {
        Span<char> letters = stackalloc char[8];
        var length = 0;
        var value = index;
        do
        {
            letters[length++] = (char)('A' + (value % 26));
            value = (value / 26) - 1;
        }
        while (value >= 0);

        for (var left = 0; left < length / 2; left++)
        {
            (letters[left], letters[length - 1 - left]) = (letters[length - 1 - left], letters[left]);
        }

        return new string(letters[..length]);
    }
}
