using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Terminal;

/// <summary>
/// The <c>terminal</c> settings section (order 400): the shell a new terminal starts, its arguments and how many
/// scrollback lines the buffer keeps. Everything edits the live <see cref="AppPreferences"/>, so a change applies to the
/// next terminal (and, for the scrollback, to the terminals that are already open) without a restart.
/// </summary>
internal sealed class TerminalSettingsViewModel : ObservableObject
{
    private readonly AppPreferences preferences;

    public TerminalSettingsViewModel(AppPreferences preferences)
    {
        this.preferences = preferences;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string ShellLabel => Loc.Instance[TerminalStrings.SettingsShell];

    public string ArgumentsLabel => Loc.Instance[TerminalStrings.SettingsArguments];

    public string ScrollbackLabel => Loc.Instance[TerminalStrings.SettingsScrollback];

    public string Hint => Loc.Instance[TerminalStrings.SettingsHint];

    /// <summary>Shell executable of new terminals; empty means the platform default, as the hint says.</summary>
    public string Shell
    {
        get => preferences.TerminalShell;
        set
        {
            preferences.TerminalShell = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Arguments of the shell, space separated with double quotes for grouping.</summary>
    public string Arguments
    {
        get => preferences.TerminalArguments;
        set
        {
            preferences.TerminalArguments = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Scrollback line count; the preferences setter clamps it to the supported range.</summary>
    public decimal? ScrollbackLines
    {
        get => preferences.TerminalScrollbackLines;
        set
        {
            if (value is not { } lines)
            {
                return;
            }

            var clamped = (int)Math.Clamp(
                Math.Round(lines, MidpointRounding.AwayFromZero),
                AppPreferences.MinScrollbackLines,
                AppPreferences.MaxScrollbackLines);
            preferences.TerminalScrollbackLines = clamped;
            OnPropertyChanged();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ShellLabel));
        OnPropertyChanged(nameof(ArgumentsLabel));
        OnPropertyChanged(nameof(ScrollbackLabel));
        OnPropertyChanged(nameof(Hint));
    }
}
