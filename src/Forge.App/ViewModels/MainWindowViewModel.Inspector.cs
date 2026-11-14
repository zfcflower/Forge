namespace Forge.App.ViewModels;

/// <summary>
/// The window's the resource inspector slot (wave W8 owns this file). <see cref="Inspector"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? inspector;

    public object? Inspector
    {
        get => inspector;
        private set => Set(ref inspector, value);
    }
}
