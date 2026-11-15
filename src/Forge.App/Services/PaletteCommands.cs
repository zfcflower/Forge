using Forge.App.Input;
using Forge.App.Localization;

namespace Forge.App.Services;

/// <summary>
/// One command the palette offers: a stable id (what <c>MainWindowViewModel.RunCommand</c> switches on) and the keys of its
/// title and category. The gesture that also reaches it from the window is resolved from the keymap catalog, so a rebind
/// changes the badge and the source strip without touching this table.
/// </summary>
internal sealed record PaletteCommand(string Id, string TitleKey, string CategoryKey)
{
    public string Title => Loc.Instance[TitleKey];

    public string Category => Loc.Instance[CategoryKey];

    /// <summary>The opening gesture, or empty when the command has none.</summary>
    public string Gesture => Id switch
    {
        PaletteCommandIds.Files => KeymapApplier.DisplayGesture(KeymapActionIds.OpenFilePalette),
        PaletteCommandIds.Content => KeymapApplier.DisplayGesture(KeymapActionIds.OpenSearchPalette),
        PaletteCommandIds.Commands => KeymapApplier.DisplayGesture(KeymapActionIds.OpenCommandPalette),
        PaletteCommandIds.Terminal => KeymapApplier.DisplayGesture(KeymapActionIds.NewTerminal),
        _ => string.Empty,
    };
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
    private static readonly PaletteCommand[] Items =
    [
        new(PaletteCommandIds.Files, PaletteStrings.CommandFiles, PaletteStrings.CategoryNavigate),
        new(PaletteCommandIds.Content, PaletteStrings.CommandContent, PaletteStrings.CategoryNavigate),
        new(PaletteCommandIds.Sessions, PaletteStrings.CommandSessions, PaletteStrings.CategoryNavigate),
        new(PaletteCommandIds.Symbols, PaletteStrings.CommandSymbols, PaletteStrings.CategoryNavigate),
        new(PaletteCommandIds.Commands, PaletteStrings.CommandCommands, PaletteStrings.CategoryNavigate),
        new(PaletteCommandIds.Changes, PaletteStrings.CommandChanges, PaletteStrings.CategoryView),
        new(PaletteCommandIds.Terminal, PaletteStrings.CommandTerminal, PaletteStrings.CategoryView),
        new(PaletteCommandIds.ToggleProjects, PaletteStrings.CommandToggleProjects, PaletteStrings.CategoryView),
        new(PaletteCommandIds.ToggleFiles, PaletteStrings.CommandToggleFiles, PaletteStrings.CategoryView),
        new(PaletteCommandIds.TogglePreview, PaletteStrings.CommandTogglePreview, PaletteStrings.CategoryView),
        new(PaletteCommandIds.TodoTab, PaletteStrings.CommandTodoTab, PaletteStrings.CategoryView),
        new(PaletteCommandIds.ReviewTab, PaletteStrings.CommandReviewTab, PaletteStrings.CategoryView),
        new(PaletteCommandIds.SpecsTab, PaletteStrings.CommandSpecsTab, PaletteStrings.CategoryView),
        new(PaletteCommandIds.AddProject, PaletteStrings.CommandAddProject, PaletteStrings.CategoryProject),
        new(PaletteCommandIds.RevealGeneral, PaletteStrings.CommandRevealGeneral, PaletteStrings.CategoryProject),
        new(PaletteCommandIds.OpenSettings, PaletteStrings.CommandOpenSettings, PaletteStrings.CategorySettings),
        new(PaletteCommandIds.ReloadSettings, PaletteStrings.CommandReloadSettings, PaletteStrings.CategorySettings),
        new(PaletteCommandIds.GitFetch, PaletteStrings.CommandGitFetch, PaletteStrings.CategoryGit),
        new(PaletteCommandIds.GitPull, PaletteStrings.CommandGitPull, PaletteStrings.CategoryGit),
        new(PaletteCommandIds.GitPush, PaletteStrings.CommandGitPush, PaletteStrings.CategoryGit),
    ];

    /// <summary>The catalog in display order; a caller may hold the array (it is immutable).</summary>
    public static IReadOnlyList<PaletteCommand> All => Items;

    /// <summary>True when <paramref name="id"/> is a command of the catalog.</summary>
    public static bool Contains(string? id) => Items.Any(command => string.Equals(command.Id, id, StringComparison.Ordinal));
}
