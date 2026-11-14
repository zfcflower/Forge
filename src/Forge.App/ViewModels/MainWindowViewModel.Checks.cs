namespace Forge.App.ViewModels;

/// <summary>
/// The window's the pull-request checks panel slot (wave W5 owns this file). <see cref="Checks"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? checks;

    public object? Checks
    {
        get => checks;
        private set => Set(ref checks, value);
    }
}
