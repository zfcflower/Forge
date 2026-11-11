using Avalonia.Controls;
using Forge.App.Providers;

namespace Forge.App.Settings;

/// <summary>
/// The "登录" dialog of a built-in provider. It closes with the connected definition when the sign-in succeeds. The Claude
/// guidance is re-checked whenever the window is activated again, because the user usually signs in in a terminal.
/// </summary>
internal sealed partial class ProviderLoginWindow : Window
{
    private readonly ProviderLoginViewModel? model;

    /// <summary>Designer/loader constructor.</summary>
    public ProviderLoginWindow()
    {
        InitializeComponent();
    }

    public ProviderLoginWindow(ProviderLoginViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        model = viewModel;
        DataContext = viewModel;
        viewModel.Done += OnDone;
        viewModel.Cancelled += OnCancelled;
        viewModel.OpenUrl += OnOpenUrl;
        Activated += OnActivatedAgain;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (model is not null)
        {
            model.Done -= OnDone;
            model.Cancelled -= OnCancelled;
            model.OpenUrl -= OnOpenUrl;
            model.Dispose();
        }

        base.OnClosed(e);
    }

    private void OnActivatedAgain(object? sender, EventArgs e) => _ = model?.RecheckAsync();

    private void OnDone(object? sender, ProviderDefinition definition) => Close(definition);

    private void OnCancelled(object? sender, EventArgs e) => Close((ProviderDefinition?)null);

    private void OnOpenUrl(object? sender, string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            _ = Launcher.LaunchUriAsync(parsed);
        }
    }
}
