using Forge.App.Localization;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// A CSV/TSV table preview: the parsed rows (up to the configured limit), the header row when the heuristic found one,
/// and precomputed column widths (widest cell of the first <see cref="WidthSampleRows"/> rows, capped). The parse runs
/// off the UI thread; the view renders one virtualized row per table row, never the whole table as one text blob.
/// </summary>
internal sealed class TableFilePreviewViewModel : FilePreviewViewModel
{
    /// <summary>Column widths are computed over this many rows at most.</summary>
    internal const int WidthSampleRows = 200;

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

    public bool HasHeader => Header is not null;

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
            var hasHeader = TablePreviewReader.HasHeaderRow(parsed.Rows);
            var header = hasHeader ? new TablePreviewRowViewModel(CellsFor(parsed.Rows[0], widths, charWidth), isHeader: true) : null;

            var rows = new TablePreviewRowViewModel[parsed.Rows.Count - (hasHeader ? 1 : 0)];
            for (var r = 0; r < rows.Length; r++)
            {
                rows[r] = new TablePreviewRowViewModel(CellsFor(parsed.Rows[r + (hasHeader ? 1 : 0)], widths, charWidth), isHeader: false);
            }

            return new TableFilePreviewViewModel(path, name, byteSize, header, rows, rows.Length, parsed.SkippedRows, format);
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

    private static IReadOnlyList<TablePreviewCellViewModel> CellsFor(IReadOnlyList<string> row, int[] widths, double charWidth)
    {
        var cells = new TablePreviewCellViewModel[widths.Length];
        for (var column = 0; column < widths.Length; column++)
        {
            cells[column] = new TablePreviewCellViewModel(column < row.Count ? row[column] : string.Empty, widths[column], charWidth);
        }

        return cells;
    }
}

/// <summary>One table row: the cells with their precomputed widths; the view stacks them horizontally.</summary>
internal sealed class TablePreviewRowViewModel
{
    public TablePreviewRowViewModel(IReadOnlyList<TablePreviewCellViewModel> cells, bool isHeader)
    {
        Cells = cells;
        IsHeader = isHeader;
    }

    public IReadOnlyList<TablePreviewCellViewModel> Cells { get; }

    public bool IsHeader { get; }
}

/// <summary>One table cell: the text and its column width in monospace characters (times a fixed advance, capped).</summary>
internal sealed class TablePreviewCellViewModel
{
    public TablePreviewCellViewModel(string text, int widthChars, double charWidth)
    {
        Text = text;
        Width = widthChars * charWidth;
    }

    public string Text { get; }

    public double Width { get; }
}
