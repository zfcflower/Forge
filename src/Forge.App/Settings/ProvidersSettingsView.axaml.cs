using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.Core.Providers;

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

    /// <summary>
    /// "Add server" is the free-form entry: it starts on the custom preset (no URL, OpenAI-compatible) instead of a vendor,
    /// because a button that sits under "custom server" must not pre-fill someone else's endpoint.
    /// </summary>
    private void OnAddClick(object? sender, RoutedEventArgs e) => _ = OpenEditorAsync(null, ProviderPresets.Custom);

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

    private async Task OpenEditorAsync(ProviderRowViewModel? row, ProviderPreset? startPreset = null)
    {
        if (Model is not { } page || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new ProviderEditorViewModel(
            page.Services,
            row?.Provider.Definition,
            row?.IsDefault ?? false,
            startPreset,
            [.. page.Services.Catalog.Providers.Select(static provider => provider.Id)]);
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

        // Definition first, key second: if the settings write is refused the existing key stays exactly as it was.
        var saved = await page.SaveAsync(definition, makeDefaultId: result.MakeDefault ? definition.Id : null);
        if (!saved)
        {
            return;
        }

        if (result.Secret is { Length: > 0 } secret)
        {
            await page.SetSecretAsync(definition, secret);
        }
        else if (result.ClearSecret)
        {
            await page.ClearSecretAsync(definition);
        }
    }
}
