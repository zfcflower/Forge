namespace Forge.App.ViewModels;

/// <summary>
/// The window's the first-run wizard slot (wave W9 owns this file). <see cref="Onboarding"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private object? onboarding;

    public object? Onboarding
    {
        get => onboarding;
        private set => Set(ref onboarding, value);
    }
}
