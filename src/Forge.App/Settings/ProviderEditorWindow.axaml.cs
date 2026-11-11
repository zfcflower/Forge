using Avalonia.Controls;
using Forge.App.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The shared add/edit provider dialog. It owns only the dialog protocol: Save (after a passing test) closes with the
/// definition, Cancel or Esc closes with nothing. The window is shared by the custom entry and the built-in API-key rows.
/// </summary>
internal sealed partial class ProviderEditorWindow : Window
{
    private readonly ProviderEditorViewModel? model;
    private bool saved;

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
    }

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.Saved -= OnSaved;
            model.Cancelled -= OnCancelled;
            if (!saved)
            {
                // Cancel, Esc and the close button all end here: stop a running test and drop its draft key.
                _ = model.AbandonAsync();
            }
        }

        base.OnClosed(e);
    }

    private void OnSaved(object? sender, ProviderDefinition definition)
    {
        saved = true;
        Close(definition);
    }

    private void OnCancelled(object? sender, EventArgs e) => Close((ProviderDefinition?)null);
}
