using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// The plan panel's view: the scrim that closes it, the card, the Markdown summary rendered into <c>SummaryHost</c>, and the
/// save-file dialog the export needs.
/// <para>The Markdown tree is built here rather than in the view model - the view model hands over the text, the view parses
/// and renders it - so the panel's state machine stays testable without a window. It is rebuilt only when the text actually
/// changes: a refresh that re-reads the same summary re-renders nothing.</para>
/// </summary>
public partial class PlanPanelView : UserControl
{
    private PlanPanelViewModel? model;
    private string rendered = string.Empty;

    public PlanPanelView()
    {
        InitializeComponent();
    }

    /// <summary>The keyboard lands on the close button, so Escape works the moment the panel opens.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as PlanPanelViewModel);
        CloseButton?.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Bind(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Bind(DataContext as PlanPanelViewModel);
    }

    private void Bind(PlanPanelViewModel? next)
    {
        if (ReferenceEquals(model, next))
        {
            return;
        }

        if (model is not null)
        {
            model.PropertyChanged -= OnModelPropertyChanged;
        }

        model = next;
        if (model is not null)
        {
            model.PropertyChanged += OnModelPropertyChanged;
        }

        RenderSummary();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanPanelViewModel.SummaryText))
        {
            RenderSummary();
        }
    }

    /// <summary>Parses the summary text and puts the rendered blocks into the panel; no-op while the text is unchanged.</summary>
    private void RenderSummary()
    {
        if (SummaryHost is null)
        {
            return;
        }

        var text = model?.SummaryText ?? string.Empty;
        if (string.Equals(text, rendered, StringComparison.Ordinal))
        {
            return;
        }

        rendered = text;
        SummaryHost.Children.Clear();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var blocks = MarkdownBlockParser.Parse(text.ReplaceLineEndings("\n").Split('\n'));
            SummaryHost.Children.Add(MarkdownRenderer.RenderBlocks(blocks));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The summary is agent-written text; a parse failure shows it as plain text instead of an empty area.
            AppLogs.Markdown.ZLogWarning($"could not render the plan summary: {exception.Message}");
            SummaryHost.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
    }

    /// <summary>A click on the scrim - never on the card - closes the panel.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Scrim))
        {
            return;
        }

        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void Close()
    {
        if (model?.CloseCommand.CanExecute(null) == true)
        {
            model.CloseCommand.Execute(null);
        }
    }

    /// <summary>
    /// The export: the window owns the save dialog, the panel owns the text. Only a path the user actually picked is written.
    /// </summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (model is not { } panel)
        {
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return;
            }

            var file = await top.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = Loc.Instance[PlanStrings.ExportTitle],
                    SuggestedFileName = panel.SuggestedExportName,
                    DefaultExtension = "md",
                    FileTypeChoices =
                    [
                        new FilePickerFileType(Loc.Instance[PlanStrings.ExportFileType]) { Patterns = ["*.md"] },
                    ],
                });
            if (file?.TryGetLocalPath() is not { Length: > 0 } path)
            {
                return;
            }

            await panel.ExportAsync(path, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"the plan export could not start: {exception.Message}");
        }
    }
}
