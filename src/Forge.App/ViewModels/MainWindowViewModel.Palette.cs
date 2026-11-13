namespace Forge.App.ViewModels;

/// <summary>
/// The window's the command palette / global search slot (wave W3 owns this file). <see cref="Palette"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? palette;

    /// <summary>
    /// The title-bar search button. Disabled until wave W3 replaces this with the command that opens the palette; the button
    /// and its binding are already in <c>MainWindow.axaml</c>.
    /// </summary>
    public RelayCommand OpenPaletteCommand { get; } = new(static () => { }, static () => false);

    public object? Palette
    {
        get => palette;
        private set => Set(ref palette, value);
    }
}
