using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

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
    private const double ReasoningWidth = 92;
    private const double ContextWidth = 102;
    private const double PriceWidth = 76;
    private const double CacheWidth = 82;

    private readonly ModelMetadataViewModel? model;
    private ModelMetadataViewModel? subscribed;
    private TableViewColumn? cacheWriteColumn;
    private CheckBox? allShown;

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

    /// <summary>The column set: the model fits the leftover width, everything else is sized for its content.</summary>
    private void BuildColumns(ModelMetadataViewModel viewModel)
    {
        allShown = null;
        cacheWriteColumn = null;
        var columns = new AvaloniaList<TableViewColumn>();

        if (viewModel.HasVisibilityColumn)
        {
            columns.Add(Column(VisibilityWidth, "MetaVisibilityCell", VisibilityHeader(viewModel)));
        }

        columns.Add(Column(new GridLength(1, GridUnitType.Star), "MetaModelCell", TextHeader(viewModel.ModelHeader)));
        columns.Add(Column(CapabilitiesWidth, "MetaCapabilitiesCell", TextHeader(viewModel.CapabilitiesHeader)));
        columns.Add(Column(ReasoningWidth, "MetaReasoningCell", TextHeader(viewModel.ReasoningHeader)));
        columns.Add(Column(ContextWidth, "MetaContextCell", TextHeader(viewModel.ContextHeader)));
        columns.Add(Column(PriceWidth, "MetaInputCell", TextHeader(viewModel.InputHeader)));
        columns.Add(Column(PriceWidth, "MetaOutputCell", TextHeader(viewModel.OutputHeader)));
        columns.Add(Column(CacheWidth, "MetaCacheReadCell", TextHeader(viewModel.CacheReadHeader)));

        cacheWriteColumn = Column(CacheWidth, "MetaCacheWriteCell", TextHeader(viewModel.CacheWriteHeader));
        cacheWriteColumn.IsVisible = viewModel.ShowsCacheWriteColumn;
        columns.Add(cacheWriteColumn);

        Table.Columns = columns;
    }

    private TableViewColumn Column(GridLength width, string cellTemplate, object header) => new()
    {
        Width = width,
        CellTemplate = this.TryFindResource(cellTemplate, out var resource) ? resource as IDataTemplate : null,
        Header = header,
    };

    private TableViewColumn Column(double width, string cellTemplate, object header)
        => Column(new GridLength(width), cellTemplate, header);

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
        check.IsCheckedChanged += (_, _) => viewModel.AllShown = check.IsChecked == true;
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
                    allShown.IsChecked = viewModel.AllShown;
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
