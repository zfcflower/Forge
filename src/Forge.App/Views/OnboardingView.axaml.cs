using Avalonia.Controls;

namespace Forge.App.Views;

/// <summary>
/// The view of <see cref="ViewModels.OnboardingViewModel"/>: the first-run wizard overlay. Pure XAML — the mask, the card
/// and every step's controls are declared in the markup, because the view model already exposes everything as properties
/// (the computed sentences included) and the template in <c>App.axaml</c> finds it by its type.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
}
