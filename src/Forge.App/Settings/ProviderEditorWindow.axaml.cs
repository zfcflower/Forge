using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Localization;
using Forge.App.Providers;

namespace Forge.App.Settings;

/// <summary>What the editor closes with: a saved definition, a confirmed deletion, or neither (cancel).</summary>
internal sealed record ProviderEditorResult(ProviderDefinition? Definition, string? DeletedId, bool MakeDefault);

/// <summary>
/// The add/edit provider dialog. It owns the dialog protocol only: Esc/Esc-like cancel closes with nothing, Save closes
/// with the validated definition, and Delete needs a second click because the row it removes carries a credential.
/// </summary>
internal sealed partial class ProviderEditorWindow : Window
{
    private readonly ProviderEditorViewModel? model;
    private bool deleteConfirmed;

    /// <summary>Designer/loader constructor.</summary>
    public ProviderEditorWindow()
    {
        InitializeComponent();
    }

    public ProviderEditorWindow(ProviderEditorViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
        viewModel.Cancelled += OnCancelled;
        viewModel.DeleteRequested += OnDeleteRequested;
        viewModel.OpenBrowser += OnOpenBrowser;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.Saved -= OnSaved;
            model.Cancelled -= OnCancelled;
            model.DeleteRequested -= OnDeleteRequested;
            model.OpenBrowser -= OnOpenBrowser;
            model.Dispose();
        }

        base.OnClosed(e);
    }

    private void OnSaved(object? sender, ProviderDefinition definition) =>
        Close(new ProviderEditorResult(definition, DeletedId: null, model?.MakeDefault ?? false));

    private void OnCancelled(object? sender, EventArgs e) => Close((ProviderEditorResult?)null);

    private void OnDeleteRequested(object? sender, string id) =>
        Close(new ProviderEditorResult(Definition: null, DeletedId: id, MakeDefault: false));

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close((ProviderEditorResult?)null);

    private void OnAddModelClick(object? sender, RoutedEventArgs e)
    {
        if (model is null)
        {
            return;
        }

        var text = NewModelBox.Text;
        model.AddModel(text ?? string.Empty);
        NewModelBox.Text = string.Empty;
    }

    private void OnRemoveModelClick(object? sender, RoutedEventArgs e)
    {
        if (model is not null && sender is Button { DataContext: string id })
        {
            model.RemoveModel(id);
        }
    }

    /// <summary>
    /// Delete takes two clicks: the first turns the button into the confirmation sentence, the second deletes. A destructive
    /// action on a row that holds a credential should not be one stray click away, and a modal confirmation on top of a modal
    /// dialog is worse than an inline one.
    /// </summary>
    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (model is null || !model.CanDelete)
        {
            return;
        }

        if (!deleteConfirmed)
        {
            deleteConfirmed = true;
            if (sender is Button button)
            {
                button.Content = Loc.Instance[Localization.ProviderStrings.EditorDeleteConfirmKey];
            }

            return;
        }

        model.RequestDelete();
    }

    private void OnOpenBrowser(object? sender, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            _ = Launcher.LaunchUriAsync(parsed);
        }
    }
}
