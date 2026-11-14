using Forge.App.Localization;

namespace Forge.App.Services;

/// <summary>
/// One command the palette offers: a stable id (what <c>MainWindowViewModel.RunCommand</c> switches on), the keys of its
/// title and category, and the gesture that also reaches it from the window. Both texts are localization keys, so the
/// row follows a language switch when the list is rebuilt.
/// </summary>
internal sealed record PaletteCommand(string Id, string TitleKey, string CategoryKey, string Gesture)
{
    public string Title => Loc.Instance[TitleKey];

    public string Category => Loc.Instance[CategoryKey];
}

/// <summary>
/// The ids of the commands of <see cref="PaletteCommandCatalog"/>. They are the contract between the catalog and the
/// shell's switch, so a row can never name an action nothing implements.
/// </summary>
internal static class PaletteCommandIds
{
    public const string Files = "palette.files";
    public const string Content = "palette.content";
    public const string Sessions = "palette.sessions";
    public const string Symbols = "palette.symbols";
    public const string Commands = "palette.commands";

    public const string Changes = "view.changes";
    public const string Terminal = "view.terminal";
    public const string ToggleProjects = "view.projects";
    public const string ToggleFiles = "view.files";
    public const string TogglePreview = "view.preview";
    public const string TodoTab = "view.todo";
    public const string ReviewTab = "view.reviews";
    public const string SpecsTab = "view.specs";

    public const string AddProject = "project.add";
    public const string RevealGeneral = "project.revealGeneral";

    public const string OpenSettings = "settings.open";
    public const string ReloadSettings = "settings.reload";

    public const string GitFetch = "git.fetch";
    public const string GitPull = "git.pull";
    public const string GitPush = "git.push";
}

/// <summary>
/// Every command the palette can run, in the order the empty command list shows them: navigation first, then the view
/// toggles, the project and settings actions and finally git. The catalog is static data (no state, nothing to dispose),
/// so building it costs nothing and the shell only has to switch on <see cref="PaletteCommand.Id"/>.
/// </summary>
internal static class PaletteCommandCatalog
{
    /// <summary>The gesture texts, spelled the way the window's bindings are written.</summary>
    public const string FilesGesture = "Ctrl+P";

    public const string CommandsGesture = "Ctrl+Shift+P";
    public const string ContentGesture = "Ctrl+Shift+F";
    public const string TerminalGesture = "Ctrl+`";

    private static readonly PaletteCommand[] Items =
    [
        new(PaletteCommandIds.Files, PaletteStrings.CommandFiles, PaletteStrings.CategoryNavigate, FilesGesture),
        new(PaletteCommandIds.Content, PaletteStrings.CommandContent, PaletteStrings.CategoryNavigate, ContentGesture),
        new(PaletteCommandIds.Sessions, PaletteStrings.CommandSessions, PaletteStrings.CategoryNavigate, string.Empty),
        new(PaletteCommandIds.Symbols, PaletteStrings.CommandSymbols, PaletteStrings.CategoryNavigate, string.Empty),
        new(PaletteCommandIds.Commands, PaletteStrings.CommandCommands, PaletteStrings.CategoryNavigate, CommandsGesture),
        new(PaletteCommandIds.Changes, PaletteStrings.CommandChanges, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.Terminal, PaletteStrings.CommandTerminal, PaletteStrings.CategoryView, TerminalGesture),
        new(PaletteCommandIds.ToggleProjects, PaletteStrings.CommandToggleProjects, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.ToggleFiles, PaletteStrings.CommandToggleFiles, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.TogglePreview, PaletteStrings.CommandTogglePreview, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.TodoTab, PaletteStrings.CommandTodoTab, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.ReviewTab, PaletteStrings.CommandReviewTab, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.SpecsTab, PaletteStrings.CommandSpecsTab, PaletteStrings.CategoryView, string.Empty),
        new(PaletteCommandIds.AddProject, PaletteStrings.CommandAddProject, PaletteStrings.CategoryProject, string.Empty),
        new(PaletteCommandIds.RevealGeneral, PaletteStrings.CommandRevealGeneral, PaletteStrings.CategoryProject, string.Empty),
        new(PaletteCommandIds.OpenSettings, PaletteStrings.CommandOpenSettings, PaletteStrings.CategorySettings, string.Empty),
        new(PaletteCommandIds.ReloadSettings, PaletteStrings.CommandReloadSettings, PaletteStrings.CategorySettings, string.Empty),
        new(PaletteCommandIds.GitFetch, PaletteStrings.CommandGitFetch, PaletteStrings.CategoryGit, string.Empty),
        new(PaletteCommandIds.GitPull, PaletteStrings.CommandGitPull, PaletteStrings.CategoryGit, string.Empty),
        new(PaletteCommandIds.GitPush, PaletteStrings.CommandGitPush, PaletteStrings.CategoryGit, string.Empty),
    ];

    /// <summary>The catalog in display order; a caller may hold the array (it is immutable).</summary>
    public static IReadOnlyList<PaletteCommand> All => Items;

    /// <summary>True when <paramref name="id"/> is a command of the catalog.</summary>
    public static bool Contains(string? id) => Items.Any(command => string.Equals(command.Id, id, StringComparison.Ordinal));
}
