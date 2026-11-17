using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
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
    private const double ContextWidth = 102;
    private const double PriceWidth = 76;
    private const double CacheWidth = 82;

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
    /// True while the header switch is being mirrored from the rows. Every row change re-raises <c>AllShown</c>, and writing
    /// that back into the model looks like a second click: checking one row would clear the rest all over again.
    /// </summary>
    private bool syncingAllShown;

    /// <summary>Designer/loader constructor.</summary>
    public ModelMetadataWindow()
    {
        InitializeComponent();
        Table.ContainerPrepared += OnRowPrepared;
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

        if (viewModel.HasVisibilityColumn)
        {
            Add(VisibilityMinWidth, new GridLength(VisibilityWidth), "MetaVisibilityCell", VisibilityHeader(viewModel));
        }

        Add(ModelMinWidth, new GridLength(1, GridUnitType.Star), "MetaModelCell", TextHeader(viewModel.ModelHeader));
        Add(CapabilitiesMinWidth, new GridLength(CapabilitiesWidth), "MetaCapabilitiesCell", TextHeader(viewModel.CapabilitiesHeader));
        // The tier list is the longest text in the row and was clipped at 92 px: it gets a fixed width that fits it whole, and the
        // model name keeps the leftover so widening the window never squeezes the list again.
        Add(ReasoningMinWidth, new GridLength(ReasoningWidth), "MetaReasoningCell", TextHeader(viewModel.ReasoningHeader));
        Add(ContextMinWidth, new GridLength(ContextWidth), "MetaContextCell", TextHeader(viewModel.ContextHeader));
        Add(PriceMinWidth, new GridLength(PriceWidth), "MetaInputCell", TextHeader(viewModel.InputHeader));
        Add(PriceMinWidth, new GridLength(PriceWidth), "MetaOutputCell", TextHeader(viewModel.OutputHeader));
        Add(CacheMinWidth, new GridLength(CacheWidth), "MetaCacheReadCell", TextHeader(viewModel.CacheReadHeader));
        cacheWriteColumn = Add(CacheMinWidth, new GridLength(CacheWidth), "MetaCacheWriteCell", TextHeader(viewModel.CacheWriteHeader));
        cacheWriteColumn.IsVisible = viewModel.ShowsCacheWriteColumn;

        Table.Columns = columns;
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

    /// <summary>The visibility header: the select-all switch over the column's own checkboxes.</summary>
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
        panel.Children.Add(check);
        panel.Children.Add(TextHeader(viewModel.VisibilityHeader));
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
