using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using HAlign = Avalonia.Layout.HorizontalAlignment;

namespace Forge.App.Settings;

/// <summary>
/// The model metadata window. Non-modal, one per provider: the user can leave it open while editing a provider, and the
/// window disposes its page model when it closes.
/// <para>The table is a <see cref="TableView"/>; its columns are built here because their headers are localized and the
/// cache-write column is conditional. Each cell's markup lives in the window resources (one template per column), so the
/// control owns the layout, the scrolling and the row virtualization while the window only decides the column set.</para>
/// </summary>
internal sealed partial class ModelMetadataWindow : Window
{
    private const double VisibilityWidth = 84;
    private const double CapabilitiesWidth = 124;
    private const double ContextWidth = 120;
    private const double PriceWidth = 94;
    private const double CacheWidth = 94;

    /// <summary>
    /// The tier list's column. 158 px is the Chinese list (「极低/低/中/高/超高」 is ~94 px of text) plus the icon, the padding and
    /// room to breathe; the English labels ("Minimal/Low/Medium/High/Extra high") are longer and get the cell's tooltip instead.
    /// The model name takes the leftover width.
    /// </summary>
    private const double ReasoningWidth = 158;

    // The floor each column keeps when the user drags its separator: its header text plus one icon or one short number. The
    // reasoning floor is the width the longest tier list (「极低/低/中/高/超高」) needs, measured with the row font.
    private const double VisibilityMinWidth = 48;
    private const double ModelMinWidth = 80;
    private const double CapabilitiesMinWidth = 88;
    private const double ReasoningMinWidth = 124;
    private const double ContextMinWidth = 72;
    private const double PriceMinWidth = 56;
    private const double CacheMinWidth = 60;

    private readonly ModelMetadataViewModel? model;
    private ModelMetadataViewModel? subscribed;
    private TableViewColumn? cacheWriteColumn;
    private CheckBox? allShown;

    /// <summary>
    /// The five number columns' field templates. The column itself carries the resting text; the field is built into the
    /// clicked cell on demand, because a live <c>NumericUpDown</c> in every cell is what made the table cost 275-390 ms to
    /// open and 260-300 ms per screen of scrolling (bench: <c>table.metadata.*</c>).
    /// </summary>
    private readonly Dictionary<TableViewColumn, IDataTemplate> editors = [];

    /// <summary>The cell that is showing its number field, or null while the table is only text.</summary>
    private ContentControl? editorSlot;

    /// <summary>The resting text the open editor covers, put back when the editor closes.</summary>
    private TextBlock? editorValue;

    /// <summary>The cell body the open editor was added to, so it can be taken out again.</summary>
    private Panel? editorBody;

    /// <summary>
    /// True from the moment a cell builds its field until that field has the keyboard. Handing it focus makes whatever held
    /// it report a focus loss, and that report is not the user leaving the editor.
    /// </summary>
    private bool openingEditor;

    /// <summary>The editable columns in the order they are drawn: the cells the arrow keys travel between.</summary>
    private readonly List<TableViewColumn> numberColumns = [];

    /// <summary>The cell the arrow keys move from, or null before the user points at one.</summary>
    private ModelMetadataRowViewModel? currentRow;
    private TableViewColumn? currentColumn;

    /// <summary>The model name column: the star that takes the width the other columns leave.</summary>
    private TableViewColumn? fillColumn;

    /// <summary>The column the user has hold of, so its own drag is not handed back to it once the drag ends.</summary>
    private TableViewColumn? dragged;

    /// <summary>
    /// True while the header switch is being mirrored from the rows. Every row change re-raises <c>AllShown</c>, and writing
    /// that back into the model looks like a second click: checking one row would clear the rest all over again.
    /// </summary>
    private bool syncingAllShown;

    /// <summary>Designer/loader constructor.</summary>
    public ModelMetadataWindow()
    {
        InitializeComponent();
        Table.ContainerPrepared += OnRowPrepared;
        // The separators are the control's own thumbs: a drag is announced before the first delta and completed after the last
        // one, which are the two moments the filler has to step aside and then take back what nobody claimed.
        Table.AddHandler(Thumb.DragStartedEvent, OnResizeStarted);
        Table.AddHandler(Thumb.DragCompletedEvent, OnResizeCompleted);
        // Editing a cell: a click or Enter builds its field, losing focus (or Enter/Escape, or scrolling the table away)
        // puts the text back.
        Table.AddHandler(PointerPressedEvent, OnCellPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnel only: a handler registered for both phases runs twice per key, which moved the highlighted cell two steps.
        Table.AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Table.AddHandler(LostFocusEvent, OnEditorFocusLost, RoutingStrategies.Bubble, handledEventsToo: true);
        Table.AddHandler(ScrollViewer.ScrollChangedEvent, OnTableScrolled, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
    }

    public ModelMetadataWindow(ModelMetadataViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.CloseRequested -= OnCloseRequested;
            model.Dispose();
        }

        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnModelPropertyChanged;
            subscribed = null;
        }

        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnModelPropertyChanged;
            subscribed = null;
        }

        if (DataContext is not ModelMetadataViewModel viewModel)
        {
            Table.Columns = new AvaloniaList<TableViewColumn>();
            return;
        }

        subscribed = viewModel;
        viewModel.PropertyChanged += OnModelPropertyChanged;
        BuildColumns(viewModel);
    }

    /// <summary>Band the rows: the container is re-prepared for every item, so a recycled row never keeps a stale class.</summary>
    private void OnRowPrepared(object? sender, ContainerPreparedEventArgs e)
        => e.Container.Classes.Set("alt", e.Index % 2 == 1);

    /// <summary>
    /// The column set: the model name takes the leftover width, the tier list is sized for its longest contents, and every
    /// column keeps a floor the user's drag cannot go below.
    /// </summary>
    private void BuildColumns(ModelMetadataViewModel viewModel)
    {
        allShown = null;
        cacheWriteColumn = null;
        fillColumn = null;
        dragged = null;
        editors.Clear();
        numberColumns.Clear();
        currentRow = null;
        currentColumn = null;
        StopEditing(refocus: false);

        // The columns are also rebuilt when their headers change language. The widths the user dragged out of the old set are
        // carried over, so switching the interface language does not undo a resize.
        var carried = CarriedWidths(viewModel);
        var next = 0;
        var columns = new AvaloniaList<TableViewColumn>();

        TableViewColumn Add(double minWidth, GridLength width, string cellTemplate, object header)
        {
            var column = new TableViewColumn
            {
                Width = carried is null ? width : carried[next],
                MinWidth = minWidth,
                CellTemplate = this.TryFindResource(cellTemplate, out var resource) ? resource as IDataTemplate : null,
                Header = header,
                // Cells draw their own grid line. The cell's default content alignment is Left, which sizes that line to the
                // text instead of to the column: the header band then shows the real edges and every row shows ragged ones.
                HorizontalContentAlignment = HAlign.Stretch,
            };
            next++;
            columns.Add(column);
            return column;
        }

        // A number column shows text, and keeps its field template aside until the user opens one of its cells.
        TableViewColumn AddNumber(double minWidth, GridLength width, string cellKey, string editorKey, string header)
        {
            var column = Add(minWidth, width, cellKey, TextHeader(header));
            if (this.TryFindResource(editorKey, out var resource) && resource is IDataTemplate field)
            {
                editors[column] = field;
                numberColumns.Add(column);
            }

            return column;
        }

        if (viewModel.HasVisibilityColumn)
        {
            Add(VisibilityMinWidth, new GridLength(VisibilityWidth), "MetaVisibilityCell", VisibilityHeader(viewModel));
        }

        fillColumn = Add(ModelMinWidth, new GridLength(1, GridUnitType.Star), "MetaModelCell", TextHeader(viewModel.ModelHeader));
        Add(CapabilitiesMinWidth, new GridLength(CapabilitiesWidth), "MetaCapabilitiesCell", TextHeader(viewModel.CapabilitiesHeader));
        // The tier list is the longest text in the row and was clipped at 92 px: it gets a fixed width that fits it whole, and the
        // model name keeps the leftover so widening the window never squeezes the list again.
        Add(ReasoningMinWidth, new GridLength(ReasoningWidth), "MetaReasoningCell", TextHeader(viewModel.ReasoningHeader));
        AddNumber(ContextMinWidth, new GridLength(ContextWidth), "MetaContextCell", "MetaContextEditor", viewModel.ContextHeader);
        AddNumber(PriceMinWidth, new GridLength(PriceWidth), "MetaInputCell", "MetaInputEditor", viewModel.InputHeader);
        AddNumber(PriceMinWidth, new GridLength(PriceWidth), "MetaOutputCell", "MetaOutputEditor", viewModel.OutputHeader);
        AddNumber(CacheMinWidth, new GridLength(CacheWidth), "MetaCacheReadCell", "MetaCacheReadEditor", viewModel.CacheReadHeader);
        cacheWriteColumn = AddNumber(
            CacheMinWidth, new GridLength(CacheWidth), "MetaCacheWriteCell", "MetaCacheWriteEditor", viewModel.CacheWriteHeader);
        cacheWriteColumn.IsVisible = viewModel.ShowsCacheWriteColumn;

        Table.Columns = columns;
    }

    /// <summary>
    /// Opens the field of the cell that was clicked, and makes it the cell the arrow keys move from. Only the clicked cell
    /// builds one: a screen of rows otherwise carries 75-95 number fields, each with a stepper, a text box and two repeat
    /// buttons of its own.
    /// </summary>
    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || source.FindAncestorOfType<TableViewCell>() is not { } cell)
        {
            return;
        }

        SetCurrent(cell);
        if (!StartEditing(cell))
        {
            // A click anywhere else in the table puts the open field away.
            StopEditing(refocus: false);
        }
    }

    /// <summary>
    /// The table's keys. With a field open the arrows do what the field needs first (the caret, or the commit) and only then
    /// move on; with the table as text they move the highlighted cell and Enter/F2 opens it. Moving while a field is open
    /// carries the editing along - the user asked for "switch the cell being edited", not just the highlight.
    /// </summary>
    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (editorSlot is not null)
        {
            OnEditingKey(e);
            return;
        }

        if (e.Key is Key.Escape)
        {
            SetCurrent(null);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.F2)
        {
            e.Handled = (CellOf(currentRow, currentColumn) ?? FirstCell()) is { } cell && StartEditing(cell);
            return;
        }

        e.Handled = e.Key switch
        {
            Key.Left => Move(rows: 0, columns: -1, editing: false),
            Key.Right => Move(rows: 0, columns: 1, editing: false),
            Key.Up => Move(rows: -1, columns: 0, editing: false),
            Key.Down => Move(rows: 1, columns: 0, editing: false),
            _ => false,
        };
    }

    private void OnEditingKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                StopEditing(refocus: true);
                e.Handled = true;
                break;
            case Key.Enter:
                // Commit and go down; in the last row there is nowhere to go, so Enter just puts the field away.
                if (!Move(rows: 1, columns: 0, editing: true))
                {
                    StopEditing(refocus: true);
                }

                e.Handled = true;
                break;
            case Key.Tab:
                e.Handled = Move(rows: 0, columns: e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1, editing: true);
                break;
            case Key.Up or Key.Down:
                e.Handled = Move(rows: e.Key == Key.Up ? -1 : 1, columns: 0, editing: true);
                break;
            case Key.Left or Key.Right when CaretAtEdge(left: e.Key == Key.Left):
                e.Handled = Move(rows: 0, columns: e.Key == Key.Left ? -1 : 1, editing: true);
                break;
        }
    }

    /// <summary>Focus left the field: the cell goes back to text, so the table keeps at most one live field.</summary>
    private void OnEditorFocusLost(object? sender, FocusChangedEventArgs e)
    {
        if (openingEditor || editorSlot is not { } slot || e.Source is not Visual source)
        {
            return;
        }

        for (Visual? node = source; node is not null; node = node.GetVisualParent())
        {
            if (ReferenceEquals(node, slot))
            {
                return;
            }
        }

        StopEditing(refocus: false);
    }

    /// <summary>
    /// Scrolling moves the rows under the editor; the field closes instead of travelling to another row. Only a moved
    /// offset counts: realizing rows changes the extent and the viewport on every layout, which is not the user scrolling.
    /// </summary>
    private void OnTableScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default && e.Source is Visual source && source.FindAncestorOfType<TableViewCell>() is null)
        {
            StopEditing(refocus: false);
        }
    }

    /// <summary>
    /// Moves the cell the arrow keys work from by one step. Left and right wrap across rows (a table is filled in a column
    /// at a time); up and down stop at the first and last model. While a field is open the move carries the editing to the new
    /// cell, which only exists after the row has been scrolled into view and laid out.
    /// </summary>
    private bool Move(int rows, int columns, bool editing)
    {
        if (DataContext is not ModelMetadataViewModel model || numberColumns.Count == 0 || model.Rows.Count == 0)
        {
            return false;
        }

        var columnIndex = currentColumn is null ? 0 : numberColumns.IndexOf(currentColumn);
        var rowIndex = currentRow is null ? 0 : model.Rows.IndexOf(currentRow);
        columnIndex = columnIndex < 0 ? 0 : columnIndex + columns;
        rowIndex = rowIndex < 0 ? 0 : rowIndex + rows;
        if (columnIndex >= numberColumns.Count)
        {
            columnIndex = 0;
            rowIndex++;
        }
        else if (columnIndex < 0)
        {
            columnIndex = numberColumns.Count - 1;
            rowIndex--;
        }

        rowIndex = Math.Clamp(rowIndex, 0, model.Rows.Count - 1);
        if (rowIndex == (currentRow is null ? -1 : model.Rows.IndexOf(currentRow)) &&
            ReferenceEquals(currentColumn, numberColumns[columnIndex]))
        {
            return false;
        }

        currentRow = model.Rows[rowIndex];
        currentColumn = numberColumns[columnIndex];
        Table.ScrollIntoView(currentRow);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowCurrent(editing), Avalonia.Threading.DispatcherPriority.Loaded);
        return true;
    }

    /// <summary>Paints the current cell once the move has been laid out, and carries the editing there.</summary>
    private void ShowCurrent(bool editing)
    {
        if (CellOf(currentRow, currentColumn) is not { } cell)
        {
            return;
        }

        SetCurrent(cell);
        if (editing)
        {
            StartEditing(cell);
        }
    }

    /// <summary>Marks one cell as the one the arrow keys move from; every other realized cell loses the mark.</summary>
    private void SetCurrent(TableViewCell? cell)
    {
        foreach (var other in Table.GetVisualDescendants().OfType<TableViewCell>())
        {
            Line(other)?.Classes.Set("current", ReferenceEquals(other, cell));
        }

        currentRow = cell?.DataContext as ModelMetadataRowViewModel;
        currentColumn = cell?.Column;
    }

    private TableViewCell? CellOf(ModelMetadataRowViewModel? row, TableViewColumn? column) =>
        row is null || column is null
            ? null
            : Table.GetVisualDescendants().OfType<TableViewCell>()
                .FirstOrDefault(cell => ReferenceEquals(cell.Column, column) && ReferenceEquals(cell.DataContext, row));

    /// <summary>The first editable cell of the first row, for Enter before any cell was pointed at.</summary>
    private TableViewCell? FirstCell()
    {
        var model = DataContext as ModelMetadataViewModel;
        return CellOf(currentRow ?? model?.Rows.FirstOrDefault(), numberColumns.FirstOrDefault());
    }

    /// <summary>Whether the caret sits at the edge an arrow would leave the field through, with nothing selected.</summary>
    private bool CaretAtEdge(bool left)
    {
        if (editorSlot?.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(box => box.Name == "PART_TextBox") is not { } text)
        {
            return false;
        }

        var nothingSelected = text.SelectionStart == text.SelectionEnd;
        return nothingSelected && (left ? text.CaretIndex <= 0 : text.CaretIndex >= (text.Text?.Length ?? 0));
    }

    private bool StartEditing(TableViewCell cell)
    {
        if (cell.Column is not { } column || !editors.TryGetValue(column, out var field))
        {
            return false;
        }

        StopEditing(refocus: false);
        if (cell.GetVisualDescendants().OfType<Panel>().FirstOrDefault(panel => panel.Name == "CellBody") is not { } body)
        {
            return false;
        }

        // The slot exists only while the cell is edited: one less control (and one less accessible-name binding) per number
        // cell of the resting table, which is what the bench measures.
        var slot = new ContentControl { Name = "EditorSlot", HorizontalContentAlignment = HAlign.Stretch };
        body.Children.Add(slot);
        editorBody = body;
        editorSlot = slot;
        editorValue = cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "ValueText");
        if (editorValue is not null)
        {
            editorValue.IsVisible = false;
        }

        openingEditor = true;
        slot.ContentTemplate = field;
        slot.Content = cell.DataContext;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusEditor(slot), Avalonia.Threading.DispatcherPriority.Loaded);
        return true;
    }

    private void FocusEditor(ContentControl slot)
    {
        if (ReferenceEquals(slot, editorSlot) &&
            slot.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.Name == "PART_TextBox") is { } text)
        {
            text.Focus();
            text.SelectAll();
        }

        // Handing the field the keyboard makes whatever held it report a focus loss, and that report is not the user leaving
        // the editor: the flag only drops once this turn's focus events have run.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => openingEditor = false,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Puts the field away and leaves the cell as text. <paramref name="refocus"/> is for the closes the table itself asks
    /// for (Enter, Escape, an arrow): those hand the keyboard back to the table so the arrows keep working, while a click
    /// somewhere else must not pull focus back.
    /// </summary>
    private void StopEditing(bool refocus)
    {
        if (editorSlot is not { } slot)
        {
            return;
        }

        editorSlot = null;
        openingEditor = false;
        slot.Content = null;
        slot.ContentTemplate = null;
        editorBody?.Children.Remove(slot);
        editorBody = null;
        if (editorValue is { } value)
        {
            value.IsVisible = true;
            editorValue = null;
        }

        if (refocus)
        {
            Table.Focus();
        }
    }

    /// <summary>The cell's own frame, which is where the tinted and highlighted states are drawn.</summary>
    private static Border? Line(TableViewCell cell) =>
        cell.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("meta-line"));

    /// <summary>
    /// A separator is about to move. While the model column takes the width the others leave, dragging a separator to its
    /// right hands the new width back out of <em>that</em> column: the total stays the width of the view, the filler loses
    /// exactly the distance dragged, and the separator under the pointer does not move at all - the column grows away from the
    /// pointer (a rightward drag on the input price used to put every cell of it left of where the drag started). Freezing the
    /// filler at the width it holds before the drag turns those drags into the table getting wider, which is what the pointer
    /// says. A separator left of the filler needs nothing: there the filler's shrinking moves nothing under the pointer, so it
    /// keeps absorbing and the table keeps fitting the window.
    /// </summary>
    private void OnResizeStarted(object? sender, VectorEventArgs e)
    {
        if (e.Source is not Visual source ||
            source.FindAncestorOfType<TableViewColumnHeader>() is not { Column: { } column } ||
            fillColumn is not { Width.IsStar: true } fill)
        {
            return;
        }

        dragged = column;
        var columns = Table.Columns;
        if (columns is null || columns.IndexOf(column) < columns.IndexOf(fill))
        {
            return;
        }

        var width = fill.ActualWidth;
        if (!double.IsNaN(width) && width > 0)
        {
            fill.Width = new GridLength(width);
        }
    }

    /// <summary>
    /// The drag is over: the width nobody claimed goes back to the model column. Without it a table the user made narrower
    /// than the view keeps the difference as a strip of window background inside its frame. Handing the star back also means
    /// a later window resize fills again.
    /// </summary>
    private void OnResizeCompleted(object? sender, VectorEventArgs e)
    {
        if (fillColumn is not { Width.IsAbsolute: true } fill ||
            ReferenceEquals(dragged, fill) ||
            Table.Columns is not { } columns)
        {
            return;
        }

        var used = fill.Width.Value;
        foreach (var column in columns)
        {
            if (column.IsVisible && !ReferenceEquals(column, fill))
            {
                used += column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
            }
        }

        if (used <= ViewportWidth())
        {
            fill.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    /// <summary>The width the columns are laid out in: the scroll viewer's viewport, minus whatever a scrollbar takes.</summary>
    private double ViewportWidth()
    {
        var viewport = Table.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Viewport.Width ?? 0;
        return viewport > 0
            ? viewport
            : Table.Bounds.Width - Table.BorderThickness.Left - Table.BorderThickness.Right;
    }

    /// <summary>
    /// The widths the current columns were dragged to, or null when the set is built for the first time. Keyed by position:
    /// the columns are always built in the same order, so a carried width only applies to the column it came from.
    /// </summary>
    private GridLength[]? CarriedWidths(ModelMetadataViewModel viewModel)
    {
        var current = Table.Columns;
        if (current is null || current.Count != ColumnCount(viewModel))
        {
            return null;
        }

        var widths = new GridLength[current.Count];
        for (var index = 0; index < widths.Length; index++)
        {
            widths[index] = current[index].Width;
        }

        return widths;
    }

    /// <summary>The number of columns <see cref="BuildColumns"/> produces, used to recognize the set it is replacing.</summary>
    private static int ColumnCount(ModelMetadataViewModel viewModel)
        => (viewModel.HasVisibilityColumn ? 1 : 0) + 8;

    private static Control TextHeader(string text) => new TextBlock { Text = text, Classes = { "meta-header" } };

    /// <summary>The visibility header: the select-all switch after the column's own title, over the column's checkboxes.</summary>
    private Control VisibilityHeader(ModelMetadataViewModel viewModel)
    {
        var check = new CheckBox
        {
            IsChecked = viewModel.AllShown,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(check, viewModel.VisibilityTooltip);
        AutomationProperties.SetAutomationId(check, "model-metadata-all-shown");
        AutomationProperties.SetName(check, viewModel.VisibilityTooltip);
        check.IsCheckedChanged += (_, _) =>
        {
            if (!syncingAllShown)
            {
                viewModel.AllShown = check.IsChecked == true;
            }
        };
        allShown = check;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(TextHeader(viewModel.VisibilityHeader));
        panel.Children.Add(check);
        return panel;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not ModelMetadataViewModel viewModel)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ModelMetadataViewModel.AllShown):
                if (allShown is not null)
                {
                    syncingAllShown = true;
                    try
                    {
                        allShown.IsChecked = viewModel.AllShown;
                    }
                    finally
                    {
                        syncingAllShown = false;
                    }
                }

                break;
            case nameof(ModelMetadataViewModel.ShowsCacheWriteColumn):
                if (cacheWriteColumn is not null)
                {
                    cacheWriteColumn.IsVisible = viewModel.ShowsCacheWriteColumn;
                }

                break;
            // The column headers are plain text handed to the table, so a language change rebuilds the columns instead of
            // pushing eleven strings into controls the table owns.
            case nameof(ModelMetadataViewModel.ModelHeader) or nameof(ModelMetadataViewModel.CapabilitiesHeader)
                or nameof(ModelMetadataViewModel.ReasoningHeader) or nameof(ModelMetadataViewModel.ContextHeader)
                or nameof(ModelMetadataViewModel.InputHeader) or nameof(ModelMetadataViewModel.OutputHeader)
                or nameof(ModelMetadataViewModel.CacheReadHeader) or nameof(ModelMetadataViewModel.CacheWriteHeader)
                or nameof(ModelMetadataViewModel.VisibilityHeader):
                BuildColumns(viewModel);
                break;
        }
    }
}
