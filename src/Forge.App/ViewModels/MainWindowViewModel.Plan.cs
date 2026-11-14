namespace Forge.App.ViewModels;

/// <summary>
/// The window's the plan panel slot (wave W7 owns this file). <see cref="Plan"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? plan;

    public object? Plan
    {
        get => plan;
        private set => Set(ref plan, value);
    }
}
