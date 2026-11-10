using Avalonia.Controls;

namespace Forge.App.Settings;

/// <summary>
/// The model metadata window. Non-modal, one per provider: the user can leave it open while editing a provider, and the
/// window disposes its page model when it closes.
/// </summary>
internal sealed partial class ModelMetadataWindow : Window
{
    private readonly ModelMetadataViewModel? model;

    /// <summary>Designer/loader constructor.</summary>
    public ModelMetadataWindow()
    {
        InitializeComponent();
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

        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
