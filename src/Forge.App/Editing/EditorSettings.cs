using Forge.App.Preferences;
using Forge.App.ViewModels;

namespace Forge.App.Editing;

/// <summary>
/// The live editor options every open editor reads: a projection of <see cref="AppPreferences"/> with a settable static
/// instance, because editors are built by a data template and cannot be handed the composition root.
/// <para>The composition root replaces <see cref="Current"/> with an instance bound to the loaded preferences before the
/// first window opens; a setter then writes through to the preferences, and their store persists it on its debounce. Tests
/// and any host without preferences keep the built-in defaults. Every setter raises <see cref="ObservableObject.PropertyChanged"/>,
/// which is what makes a change reach an editor that is already open.</para>
/// </summary>
internal sealed class EditorSettings : ObservableObject
{
    /// <summary>What an editor shows before the composition root binds the real preferences.</summary>
    private static EditorSettings current = new();

    private readonly AppPreferences? preferences;

    private bool showLineNumbers = true;
    private bool wordWrap;
    private bool showSpaces;
    private bool showTabs;
    private bool showEndOfLine;
    private bool showControlCharacters;
    private bool scrollBelowDocument = true;
    private bool highlightCurrentLine = true;
    private bool hyperlinks = true;
    private bool autoFolding = true;
    private int indentationSize = AppPreferences.DefaultEditorIndentationSize;
    private bool tabsToSpaces = true;
    private string columnRulers = string.Empty;
    private string theme = AppPreferences.DefaultEditorTheme;

    public EditorSettings(AppPreferences? preferences = null)
    {
        this.preferences = preferences;
        if (preferences is null)
        {
            return;
        }

        showLineNumbers = preferences.EditorShowLineNumbers;
        wordWrap = preferences.EditorWordWrap;
        showSpaces = preferences.EditorShowSpaces;
        showTabs = preferences.EditorShowTabs;
        showEndOfLine = preferences.EditorShowEndOfLine;
        showControlCharacters = preferences.EditorShowControlCharacters;
        scrollBelowDocument = preferences.EditorScrollBelowDocument;
        highlightCurrentLine = preferences.EditorHighlightCurrentLine;
        hyperlinks = preferences.EditorHyperlinks;
        autoFolding = preferences.EditorAutoFolding;
        indentationSize = preferences.EditorIndentationSize;
        tabsToSpaces = preferences.EditorTabsToSpaces;
        columnRulers = preferences.EditorColumnRulers;
        theme = preferences.EditorTheme;
    }

    /// <summary>The instance every editor reads; replaced once by the composition root.</summary>
    public static EditorSettings Current
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool ShowLineNumbers
    {
        get => showLineNumbers;
        set
        {
            if (Set(ref showLineNumbers, value))
            {
                preferences?.EditorShowLineNumbers = value;
            }
        }
    }

    /// <summary>The word-wrap state a newly opened file starts with; the toolbar icon still toggles it per tab.</summary>
    public bool WordWrap
    {
        get => wordWrap;
        set
        {
            if (Set(ref wordWrap, value))
            {
                preferences?.EditorWordWrap = value;
            }
        }
    }

    public bool ShowSpaces
    {
        get => showSpaces;
        set
        {
            if (Set(ref showSpaces, value))
            {
                preferences?.EditorShowSpaces = value;
            }
        }
    }

    public bool ShowTabs
    {
        get => showTabs;
        set
        {
            if (Set(ref showTabs, value))
            {
                preferences?.EditorShowTabs = value;
            }
        }
    }

    public bool ShowEndOfLine
    {
        get => showEndOfLine;
        set
        {
            if (Set(ref showEndOfLine, value))
            {
                preferences?.EditorShowEndOfLine = value;
            }
        }
    }

    public bool ShowControlCharacters
    {
        get => showControlCharacters;
        set
        {
            if (Set(ref showControlCharacters, value))
            {
                preferences?.EditorShowControlCharacters = value;
            }
        }
    }

    public bool ScrollBelowDocument
    {
        get => scrollBelowDocument;
        set
        {
            if (Set(ref scrollBelowDocument, value))
            {
                preferences?.EditorScrollBelowDocument = value;
            }
        }
    }

    public bool HighlightCurrentLine
    {
        get => highlightCurrentLine;
        set
        {
            if (Set(ref highlightCurrentLine, value))
            {
                preferences?.EditorHighlightCurrentLine = value;
            }
        }
    }

    public bool Hyperlinks
    {
        get => hyperlinks;
        set
        {
            if (Set(ref hyperlinks, value))
            {
                preferences?.EditorHyperlinks = value;
            }
        }
    }

    public bool AutoFolding
    {
        get => autoFolding;
        set
        {
            if (Set(ref autoFolding, value))
            {
                preferences?.EditorAutoFolding = value;
            }
        }
    }

    public int IndentationSize
    {
        get => indentationSize;
        set
        {
            var clamped = Math.Clamp(value, AppPreferences.MinEditorIndentationSize, AppPreferences.MaxEditorIndentationSize);
            if (Set(ref indentationSize, clamped))
            {
                preferences?.EditorIndentationSize = clamped;
            }
        }
    }

    public bool TabsToSpaces
    {
        get => tabsToSpaces;
        set
        {
            if (Set(ref tabsToSpaces, value))
            {
                preferences?.EditorTabsToSpaces = value;
            }
        }
    }

    /// <summary>Comma-separated ruler columns as typed; <see cref="ColumnRulerColumns"/> is what the editor draws.</summary>
    public string ColumnRulers
    {
        get => columnRulers;
        set
        {
            var typed = (value ?? string.Empty).Trim();
            if (Set(ref columnRulers, typed))
            {
                preferences?.EditorColumnRulers = typed;
                OnPropertyChanged(nameof(ColumnRulerColumns));
            }
        }
    }

    /// <summary>The ruler columns the editor draws; empty when the setting is blank.</summary>
    public IReadOnlyList<int> ColumnRulerColumns => AppPreferences.ParseRulers(columnRulers);

    /// <summary><c>auto</c>, <c>light</c> or <c>dark</c>; the TextMate theme follows it.</summary>
    public string Theme
    {
        get => theme;
        set
        {
            var token = AppPreferences.ThemeToken(value);
            if (Set(ref theme, token))
            {
                preferences?.EditorTheme = token;
                OnPropertyChanged(nameof(ThemeIndex));
            }
        }
    }

    /// <summary>The theme picker's index: 0 follows the UI, 1 is light, 2 is dark.</summary>
    public int ThemeIndex
    {
        get => theme switch
        {
            AppPreferences.EditorThemeLight => 1,
            AppPreferences.EditorThemeDark => 2,
            _ => 0,
        };
        set => Theme = value switch
        {
            1 => AppPreferences.EditorThemeLight,
            2 => AppPreferences.EditorThemeDark,
            _ => AppPreferences.EditorThemeAuto,
        };
    }

    /// <summary>Whether the TextMate theme should be dark for <paramref name="uiIsDark"/>; the two fixed modes ignore the UI.</summary>
    public bool IsDarkTheme(bool uiIsDark) => theme switch
    {
        AppPreferences.EditorThemeDark => true,
        AppPreferences.EditorThemeLight => false,
        _ => uiIsDark,
    };
}
