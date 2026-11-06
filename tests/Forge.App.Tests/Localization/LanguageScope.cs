using Forge.App.Localization;
using Forge.App.Preferences;

namespace Forge.App.Tests.Localization;

/// <summary>
/// Pins <see cref="Loc"/> to one language for the body of a test and restores the ambient language afterwards,
/// so one test cannot decide the next one's texts. Every test below names the language it needs up front.
/// </summary>
internal sealed class LanguageScope : IDisposable
{
    private readonly UiLanguage previous;

    public LanguageScope(UiLanguage language)
    {
        previous = Loc.Instance.Language;
        Loc.Instance.Language = language;
    }

    public void Dispose() => Loc.Instance.Language = previous;
}
