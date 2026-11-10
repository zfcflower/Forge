using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Forge.App.Settings;

/// <summary>
/// The provider section's view. It owns the two dialogs because windows belong to views: the editor is modal (the list
/// cannot change under it), the model metadata window is not (one per provider, reused while it stays open).
/// </summary>
internal sealed partial class ProvidersSettingsView : UserControl
{
    private readonly Dictionary<string, ModelMetadataWindow> metadataWindows = new(StringComparer.Ordinal);

    public ProvidersSettingsView() => InitializeComponent();

    private ProvidersSettingsViewModel? Model => DataContext as ProvidersSettingsViewModel;

    private void OnAddClick(object? sender, RoutedEventArgs e) => _ = OpenEditorAsync(null);

    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderRowViewModel row })
        {
            _ = OpenEditorAsync(row);
        }
    }

    private void OnModelsClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } page || sender is not Button { DataContext: ProviderRowViewModel row })
        {
            return;
        }

        if (metadataWindows.TryGetValue(row.Id, out var existing))
        {
            existing.Activate();
            return;
        }

        var model = new ModelMetadataViewModel(page.Services, row.Provider);
        var window = new ModelMetadataWindow(model);
        metadataWindows[row.Id] = window;
        window.Closed += (_, _) => metadataWindows.Remove(row.Id);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private async Task OpenEditorAsync(ProviderRowViewModel? row)
    {
        if (Model is not { } page || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new ProviderEditorViewModel(page.Services, row?.Provider.Definition, row?.IsDefault ?? false);
        var window = new ProviderEditorWindow(editor);
        var result = await window.ShowDialog<ProviderEditorResult?>(owner);
        if (result is null)
        {
            return;
        }

        if (result.DeletedId is { } deletedId)
        {
            await page.SaveAsync(edited: null, removedId: deletedId);
            return;
        }

        if (result.Definition is not { } definition)
        {
            return;
        }

        await page.SaveAsync(definition, makeDefaultId: result.MakeDefault ? definition.Id : null);
    }
}
