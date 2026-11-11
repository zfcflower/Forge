using Avalonia.Controls;

namespace Forge.App.Automations.Settings;

/// <summary>
/// The new/edit automation dialog. It owns only the dialog protocol: a successful save closes with <c>true</c>, Cancel,
/// Esc and the close button with <c>false</c>.
/// </summary>
internal sealed partial class AutomationEditorWindow : Window
{
    private readonly AutomationEditorViewModel? model;

    /// <summary>Designer/loader constructor.</summary>
    public AutomationEditorWindow() => InitializeComponent();

    public AutomationEditorWindow(AutomationEditorViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        DataContext = viewModel;
        viewModel.Saved += OnSaved;
        viewModel.Cancelled += OnCancelled;
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/>; true = saved.</summary>
    public static async Task<bool> ShowAsync(Window owner, AutomationEditorViewModel viewModel) =>
        await new AutomationEditorWindow(viewModel).ShowDialog<bool>(owner).ConfigureAwait(true);

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.Saved -= OnSaved;
            model.Cancelled -= OnCancelled;
        }

        base.OnClosed(e);
    }

    private void OnSaved(object? sender, Forge.Abstractions.AutomationDefinition definition) => Close(true);

    private void OnCancelled(object? sender, EventArgs e) => Close(false);
}
