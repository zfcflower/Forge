using Forge.App.Localization;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// A CSV/TSV table preview: the parsed rows (up to the configured limit), the header row when the heuristic found one,
/// and precomputed column widths (widest cell of the first <see cref="WidthSampleRows"/> rows, capped). The parse runs
/// off the UI thread; the view feeds the rows to a <c>TableView</c>, which lays out the columns and virtualizes the rows.
/// </summary>
internal sealed class TableFilePreviewViewModel : FilePreviewViewModel
{
    /// <summary>Column widths are computed over this many rows at most.</summary>
    internal const int WidthSampleRows = 200;

    /// <summary>Horizontal room a cell adds around its text (the cell's own padding plus the grid line).</summary>
    internal const double CellPadding = 14;

    /// <summary>The columns of the table: one per CSV field, carrying the content width the view sizes them by.</summary>
    public IReadOnlyList<TableColumnViewModel> Columns { get; private init; } = [];

    /// <summary>Longest cell shown before trimming, in monospace columns.</summary>
    internal const int MaxCellChars = 40;

    private TableFilePreviewViewModel(
        string path,
        string name,
        long byteSize,
        TablePreviewRowViewModel? header,
        IReadOnlyList<TablePreviewRowViewModel> rows,
        int shownRows,
        int skippedRows,
        string format)
        : base(path, name, byteSize)
    {
        Header = header;
        Rows = rows;
        ShownRows = shownRows;
        SkippedRows = skippedRows;
        Format = format;
    }

    /// <summary>The first row when the heuristic found a header; null when the first row is data.</summary>
    public TablePreviewRowViewModel? Header { get; }

    /// <summary>Data rows only (the header row is excluded from this list).</summary>
    public IReadOnlyList<TablePreviewRowViewModel> Rows { get; }

    /// <summary>Data rows shown, header excluded.</summary>
    public int ShownRows { get; }

    /// <summary>Rows beyond the limit, counted but not kept.</summary>
    public int SkippedRows { get; }

    /// <summary><c>CSV</c> or <c>TSV</c>, for the caption.</summary>
    public string Format { get; }

    public override string Caption =>
        PreviewStrings.TableCaption(Name, Format, PreviewStrings.TableSummary(ShownRows, SkippedRows));

    /// <summary>The row summary under the caption: "1,000 rows" or "showing 50, 950 skipped".</summary>
    public string Summary => PreviewStrings.TableSummary(ShownRows, SkippedRows).Resolve();

    public override void RefreshText()
    {
        base.RefreshText();
        OnPropertyChanged(nameof(Summary));
    }

    public static TableFilePreviewViewModel Build(
        string path, long byteSize, int maxRows, char separator, double charWidth, CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        var format = separator == '\t' ? "TSV" : "CSV";
        try
        {
            using var reader = new StreamReader(path);
            // Cancellable while it streams: counting the rows past the limit of a large file is the slow part.
            var parsed = TablePreviewReader.Parse(reader, maxRows, separator, ct);

            var widths = ColumnWidths(parsed.Rows);
            var columns = new TableColumnViewModel[widths.Length];
            for (var column = 0; column < columns.Length; column++)
            {
                columns[column] = new TableColumnViewModel(widths[column] * charWidth + CellPadding);
            }

            var hasHeader = TablePreviewReader.HasHeaderRow(parsed.Rows);
            var header = hasHeader ? new TablePreviewRowViewModel(CellsFor(parsed.Rows[0], columns.Length)) : null;

            var rows = new TablePreviewRowViewModel[parsed.Rows.Count - (hasHeader ? 1 : 0)];
            for (var r = 0; r < rows.Length; r++)
            {
                rows[r] = new TablePreviewRowViewModel(CellsFor(parsed.Rows[r + (hasHeader ? 1 : 0)], columns.Length));
            }

            return new TableFilePreviewViewModel(path, name, byteSize, header, rows, rows.Length, parsed.SkippedRows, format) { Columns = columns };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var viewModel = new TableFilePreviewViewModel(path, name, byteSize, null, [], 0, 0, format);
            viewModel.SetNotice(PreviewStrings.TableReadFailed(name, exception.Message));
            return viewModel;
        }
    }

    private static int[] ColumnWidths(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var widths = new int[rows[0].Count];
        var sample = Math.Min(rows.Count, WidthSampleRows);
        for (var r = 0; r < sample; r++)
        {
            var row = rows[r];
            for (var column = 0; column < row.Count; column++)
            {
                widths[column] = Math.Max(widths[column], Math.Min(TablePreviewReader.DisplayWidth(row[column]), MaxCellChars));
            }
        }

        return widths;
    }

    private static IReadOnlyList<TablePreviewCellViewModel> CellsFor(IReadOnlyList<string> row, int count)
    {
        var cells = new TablePreviewCellViewModel[count];
        for (var column = 0; column < count; column++)
        {
            cells[column] = new TablePreviewCellViewModel(column < row.Count ? row[column] : string.Empty);
        }

        return cells;
    }
}

/// <summary>One table row: one cell per column.</summary>
internal sealed class TablePreviewRowViewModel
{
    public TablePreviewRowViewModel(IReadOnlyList<TablePreviewCellViewModel> cells) => Cells = cells;

    public IReadOnlyList<TablePreviewCellViewModel> Cells { get; }
}

/// <summary>One table cell: the text a row shows in one column.</summary>
internal sealed class TablePreviewCellViewModel
{
    public TablePreviewCellViewModel(string text) => Text = text;

    public string Text { get; }
}

/// <summary>One table column: the content width the view starts the column at (a pixel width, scaled up to fill the view until the user drags a separator).</summary>
internal sealed class TableColumnViewModel
{
    public TableColumnViewModel(double baseWidth) => BaseWidth = baseWidth;

    /// <summary>The width the content needs.</summary>
    public double BaseWidth { get; }
}
