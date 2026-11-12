using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.Core.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The provider section's view. It owns the dialogs because windows belong to views: the add/edit dialog and the sign-in
/// dialog are modal (the list cannot change under them), the model metadata window is not (one per provider, reused while
/// it stays open).
/// </summary>
internal sealed partial class ProvidersSettingsView : UserControl
{
    private readonly Dictionary<string, ModelMetadataWindow> metadataWindows = new(StringComparer.Ordinal);

    public ProvidersSettingsView() => InitializeComponent();

    private ProvidersSettingsViewModel? Model => DataContext as ProvidersSettingsViewModel;

    /// <summary>The custom entry: any OpenAI-compatible server, so it starts with no URL and no vendor's key variable.</summary>
    private void OnAddClick(object? sender, RoutedEventArgs e) => _ = OpenEditorAsync(definition: null, ProviderPresets.Custom);

    /// <summary>A built-in vendor's "API 密钥" button: the same dialog, starting on that vendor's preset.</summary>
    private void OnApiKeyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BuiltInProviderRowViewModel row })
        {
            _ = OpenEditorAsync(definition: null, row.Preset);
        }
    }

    /// <summary>A built-in vendor's "登录" button: the sign-in dialog for that vendor.</summary>
    private void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } page && sender is Button { DataContext: BuiltInProviderRowViewModel row })
        {
            _ = OpenLoginAsync(page, row.Preset);
        }
    }

    /// <summary>
    /// The global context window is committed on focus loss (and on Enter) rather than per keystroke: it re-saves the
    /// section, and a half-typed number must not become a saved limit.
    /// </summary>
    private void OnDefaultWindowCommit(object? sender, RoutedEventArgs e) => _ = Model?.CommitDefaultContextWindowAsync();

    /// <summary>The compaction percentages are saved on focus loss and Enter, like the context window, never per keystroke.</summary>
    private void OnCompactionCommit(object? sender, RoutedEventArgs e) => Model?.Compaction.Commit();

    private void OnCompactionKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            e.Handled = true;
            Model?.Compaction.Commit();
        }
    }

    private void OnDefaultWindowKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            e.Handled = true;
            _ = Model?.CommitDefaultContextWindowAsync();
        }
    }

    /// <summary>
    /// The catalog address is committed on focus loss (and on Enter) for the same reason the context window is: it re-saves
    /// the section and may start a fetch, and a half-typed URL must not become a saved source.
    /// </summary>
    private void OnCatalogSourceCommit(object? sender, RoutedEventArgs e) => _ = Model?.CommitCatalogSourceAsync();

    private void OnCatalogSourceKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            e.Handled = true;
            _ = Model?.CommitCatalogSourceAsync();
        }
    }

    private void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProviderRowViewModel row })
        {
            _ = OpenEditorAsync(row.Provider.Definition, preset: null);
        }
    }

    private void OnMakeDefaultClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } page && sender is Button { DataContext: ProviderRowViewModel row })
        {
            _ = page.MakeDefaultAsync(row);
        }
    }

    /// <summary>Delete takes two clicks: the first arms the button, the second removes the provider.</summary>
    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (Model is { } page && sender is Button { DataContext: ProviderRowViewModel row } && row.ConfirmDelete())
        {
            await page.DeleteAsync(row);
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

    /// <summary>
    /// Opens the shared add/edit dialog. The dialog writes its own definition through <see cref="PersistAsync"/> and closes
    /// only once that succeeds, so a refused write keeps the dialog open with the reason.
    /// </summary>
    private async Task OpenEditorAsync(ProviderDefinition? definition, ProviderPreset? preset)
    {
        if (Model is not { } page || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var editor = new ProviderEditorViewModel(
            page.Services,
            definition,
            preset,
            [.. page.Services.Catalog.Providers.Select(static provider => provider.Id)],
            persist: candidate => PersistAsync(page, candidate));
        await new ProviderEditorWindow(editor).ShowDialog<ProviderDefinition?>(owner);
    }

    /// <summary>Opens the sign-in dialog for a built-in vendor; a successful sign-in is persisted by the dialog itself.</summary>
    private async Task OpenLoginAsync(ProvidersSettingsViewModel page, ProviderPreset preset)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var login = new ProviderLoginViewModel(
            page.Services,
            preset,
            [.. page.Services.Catalog.Providers.Select(static provider => provider.Id)],
            persist: candidate => PersistAsync(page, candidate));
        await new ProviderLoginWindow(login).ShowDialog<ProviderDefinition?>(owner);
    }

    /// <summary>Writes one definition; <c>null</c> on success, otherwise the reason the dialog shows.</summary>
    private static async Task<string?> PersistAsync(ProvidersSettingsViewModel page, ProviderDefinition definition) =>
        await page.SaveAsync(definition).ConfigureAwait(true)
            ? null
            : page.Problem ?? ProviderStrings.SaveFailedText().Resolve();
}
