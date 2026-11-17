using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Views.Preview;

/// <summary>
/// The table preview: a <see cref="TableView"/> over the parsed rows. The column count comes from the file, so the columns
/// are built here rather than in XAML; the control itself owns the header band, the scroll bars, the column layout and the
/// row virtualization. Column widths are content widths in pixels, scaled up to fill the view while the table still fits.
/// </summary>
public partial class TablePreviewView : UserControl
{
    /// <summary>Room the columns may leave unused before they are spread over the view instead of kept at content width.</summary>
    private const double StarSlack = 1;

    private readonly IDataTemplate? cellTemplate;
    private TableViewColumnHeadersPresenter? headers;

    public TablePreviewView()
    {
        InitializeComponent();
        cellTemplate = ResolveCellTemplate();
        Table.TemplateApplied += (_, _) => EnsureHeaders();
        Table.ContainerPrepared += OnContainerPrepared;
        // The header band lives inside the control's ScrollViewer template, which is applied during the first layout pass,
        // so the first size change is the earliest point the presenter is reliably there to be collapsed.
        SizeChanged += (_, _) =>
        {
            EnsureHeaders();
            ApplyWidths();
        };
        DataContextChanged += (_, _) => BuildColumns();
    }

    /// <summary>Band the rows: the container is re-prepared for every item, so a recycled row never keeps a stale class.</summary>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => e.Container.Classes.Set("alt", e.Index % 2 == 1);

    private IDataTemplate? ResolveCellTemplate()
        => this.TryFindResource("TableCellTemplate", out var resource) ? resource as IDataTemplate : null;

    private void BuildColumns()
    {
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
            var text = header is not null && column < header.Cells.Count ? header.Cells[column].Text : string.Empty;
            columns.Add(new TableViewColumn
            {
                Header = text,
                Width = new GridLength(table.Columns[column].BaseWidth),
                CellTemplate = BuildCellTemplate(column),
            });
        }

        Table.Columns = columns;
        EnsureHeaders();
        ApplyWidths();
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
    /// </summary>
    private void ApplyWidths()
    {
        if (DataContext is not TableFilePreviewViewModel table || table.Columns.Count != Table.Columns.Count)
        {
            return;
        }

        var available = Table.Bounds.Width;
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

        // Rounded down so a filled table never asks for a pixel more than it was given (that extra pixel would grow a
        // scrollbar and shift every cell by it).
        var scale = content < available - StarSlack ? available / content : 1;
        for (var index = 0; index < table.Columns.Count; index++)
        {
            var width = new GridLength(Math.Floor(table.Columns[index].BaseWidth * scale));
            if (!Table.Columns[index].Width.Equals(width))
            {
                Table.Columns[index].Width = width;
            }
        }
    }

    /// <summary>
    /// A table without a header row collapses the band. Its cells are hidden rather than the whole presenter: the presenter is
    /// what computes the column widths, and a hidden one leaves the cells to measure against infinity (and fall back to a
    /// made-up 1000 px budget, which then spills the table sideways).    /// </summary>
    private void EnsureHeaders()
    {
        headers ??= Table.GetVisualDescendants().OfType<TableViewColumnHeadersPresenter>().FirstOrDefault();

        if (headers is not null && DataContext is TableFilePreviewViewModel table)
        {
            foreach (var header in headers.Children)
            {
                header.IsVisible = table.HasHeader;
            }
        }
    }
}
