using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Editor.Lsp;
using Forge.IO;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// Pins what each of the five palette sources turns its snapshot into: the primary and secondary line of a row, the
/// badge and the jump target, the ranking each source uses and the configured limit. Also pins the exclusion rule the
/// content source applies to a result path and the localized word a symbol kind is badged with.
/// </summary>
public sealed class PaletteSourcesTests
{
    // ---------------------------------------------------------------- files

    [Fact]
    public void A_file_row_is_the_name_inside_its_directory()
    {
        IReadOnlyList<string> files = ["src/Forge.App/Views/SessionView.axaml"];

        var items = PaletteSources.Files(files, "session", 10);

        var item = Assert.Single(items);
        Assert.Equal(PaletteSource.Files, item.Source);
        Assert.Equal(PaletteItemKind.File, item.Kind);
        Assert.Equal("SessionView.axaml", item.Title);
        Assert.Equal("src/Forge.App/Views", item.Detail);
        Assert.Equal("src/Forge.App/Views/SessionView.axaml", item.Path);
        Assert.Equal(0, item.Line);
    }

    [Fact]
    public void A_file_at_the_workspace_root_has_no_detail()
    {
        var items = PaletteSources.Files(["README.md"], string.Empty, 10);

        var item = Assert.Single(items);
        Assert.Equal("README.md", item.Title);
        Assert.Equal(string.Empty, item.Detail);
        Assert.False(item.HasDetail);
        Assert.Equal("README.md", item.Path);
    }

    [Fact]
    public void Files_rank_by_name_prefix_first_then_by_shorter_path()
    {
        IReadOnlyList<string> files =
        [
            "src/other/Resession.cs",
            "app/Windows/SessionList.axaml",
            "src/session-notes.txt",
            "docs/session.md",
        ];

        var items = PaletteSources.Files(files, "session", 10);

        Assert.Equal(
            ["session.md", "session-notes.txt", "SessionList.axaml", "Resession.cs"],
            items.Select(static item => item.Title).ToArray());
        Assert.Equal(
            ["docs", "src", "app/Windows", "src/other"],
            items.Select(static item => item.Detail).ToArray());
    }

    [Fact]
    public void Files_stop_at_the_limit_and_drop_the_non_matching_ones()
    {
        IReadOnlyList<string> files =
        [
            "src/other/Resession.cs",
            "app/Windows/SessionList.axaml",
            "src/session-notes.txt",
            "docs/session.md",
        ];

        Assert.Equal(
            ["docs/session.md", "src/session-notes.txt"],
            PaletteSources.Files(files, "session", 2).Select(static item => item.Path!).ToArray());
        Assert.Empty(PaletteSources.Files(files, "session", 0));
        Assert.Empty(PaletteSources.Files(files, "nothing-matches-this", 10));
    }

    // ---------------------------------------------------------------- content

    [Fact]
    public void Content_keeps_matching_lines_only_and_drops_an_excluded_directory_at_any_depth()
    {
        var result = Result(
            new GrepLine("src/a.cs", 1, "kept", true),
            new GrepLine("src/a.cs", 2, "context", false),
            new GrepLine("node_modules/pkg/index.js", 3, "dependency", true),
            new GrepLine("src/NODE_MODULES/pkg/index.js", 4, "upper case segment", true),
            new GrepLine(".git/config", 5, "git file", true),
            new GrepLine("node_modulesx/pkg/index.js", 6, "prefix only", true));

        var items = PaletteSources.Content(result, ["node_modules", ".git"], 10);

        Assert.Equal(
            ["node_modulesx/pkg/index.js:6", "src/a.cs:1"],
            items.Select(static item => item.Title).ToArray());
        Assert.Equal(["prefix only", "kept"], items.Select(static item => item.Detail).ToArray());

        var item = items[1];
        Assert.Equal(PaletteSource.Content, item.Source);
        Assert.Equal(PaletteItemKind.ContentLine, item.Kind);
        Assert.Equal("src/a.cs", item.Path);
        Assert.Equal(1, item.Line);
    }

    [Fact]
    public void Content_sorts_by_path_then_line_and_stops_at_the_limit()
    {
        var result = Result(
            new GrepLine("src/b.cs", 10, "b ten", true),
            new GrepLine("src/a.cs", 20, "a twenty", true),
            new GrepLine("src/b.cs", 2, "b two", true),
            new GrepLine("src/a.cs", 3, "a three", true));

        var items = PaletteSources.Content(result, [], 10);

        Assert.Equal(
            ["src/a.cs:3", "src/a.cs:20", "src/b.cs:2", "src/b.cs:10"],
            items.Select(static item => item.Title).ToArray());
        Assert.Equal([3, 20, 2, 10], items.Select(static item => item.Line).ToArray());
        Assert.Equal(["a three", "a twenty", "b two", "b ten"], items.Select(static item => item.Detail).ToArray());

        Assert.Equal(
            ["src/a.cs:3", "src/a.cs:20", "src/b.cs:2"],
            PaletteSources.Content(result, [], 3).Select(static item => item.Title).ToArray());
    }

    [Fact]
    public void Content_says_it_was_cut_only_when_more_lines_survive_the_exclusion_than_the_limit()
    {
        var result = Result(
            new GrepLine("node_modules/x.js", 1, "x", true),
            new GrepLine("src/a.cs", 1, "a", true),
            new GrepLine("src/b.cs", 1, "b", true));

        Assert.Equal(2, PaletteSources.Content(result, ["node_modules"], 2, out var fits).Count);
        Assert.False(fits);

        Assert.Single(PaletteSources.Content(result, ["node_modules"], 1, out var cut));
        Assert.True(cut);
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("node_modules", new[] { "node_modules" })]
    [InlineData(" obj ,bin, ", new[] { "bin", "obj" })]
    [InlineData("dist;Bin\nbin\r\n\tout", new[] { "Bin", "dist", "out" })]
    [InlineData(",,;", new string[0])]
    public void ParseDirectoryNames_trims_splits_dedupes_case_insensitively_and_sorts(string? raw, string[] expected) =>
        Assert.Equal(expected, PaletteSources.ParseDirectoryNames(raw).ToArray());

    [Fact]
    public void Content_of_no_lines_or_a_zero_limit_is_empty()
    {
        Assert.Empty(PaletteSources.Content(Result(), [], 10));
        Assert.Empty(PaletteSources.Content(Result(new GrepLine("src/a.cs", 1, "x", true)), [], 0));
    }

    // ---------------------------------------------------------------- sessions

    [Fact]
    public void Sessions_skip_sub_agents_and_badge_the_archived_one()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("root-new", "Palette work", "forge", "main", At, IsArchived: false, IsSubAgent: false),
            new("root-old", "Palette review", "forge", "dev", At.AddHours(-2), IsArchived: true, IsSubAgent: false),
            new("child", "Palette sub-agent", "forge", "main", At.AddHours(1), IsArchived: false, IsSubAgent: true),
        ];

        var items = PaletteSources.Sessions(sessions, "palette", 10);

        Assert.Equal(["root-new", "root-old"], items.Select(static item => item.SessionId!).ToArray());
        Assert.Equal("Palette work", items[0].Title);
        Assert.Equal("forge · main", items[0].Detail);
        Assert.Equal(string.Empty, items[0].Badge);
        Assert.Equal(PaletteSource.Sessions, items[0].Source);
        Assert.Equal(PaletteItemKind.Session, items[0].Kind);

        Assert.Equal(Loc.Instance[PaletteStrings.SessionArchived], items[1].Badge);
        Assert.True(items[1].HasBadge);
        Assert.Equal("forge · dev", items[1].Detail);
    }

    [Fact]
    public void Sessions_rank_a_title_match_above_a_newer_project_match()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("project-hit", "Unrelated task", "palette", "main", At.AddHours(1), IsArchived: false, IsSubAgent: false),
            new("title-hit", "Palette work", "elsewhere", "main", At, IsArchived: false, IsSubAgent: false),
        ];

        var items = PaletteSources.Sessions(sessions, "palette", 10);

        Assert.Equal(["title-hit", "project-hit"], items.Select(static item => item.SessionId!).ToArray());
    }

    [Fact]
    public void A_session_without_a_project_shows_its_branch_alone()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("s", "Session", string.Empty, "feature/palette", At, IsArchived: false, IsSubAgent: false),
        ];

        var items = PaletteSources.Sessions(sessions, string.Empty, 10);

        var item = Assert.Single(items);
        Assert.Equal("feature/palette", item.Detail);
    }

    [Fact]
    public void A_session_without_a_branch_shows_its_project_without_a_dangling_separator()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("s", "Session", "forge", string.Empty, At, IsArchived: false, IsSubAgent: false),
        ];

        var item = Assert.Single(PaletteSources.Sessions(sessions, string.Empty, 10));

        Assert.Equal("forge", item.Detail);
    }

    [Fact]
    public void A_session_query_matches_the_branch_when_there_is_no_project()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("s", "Session", string.Empty, "feature/palette", At, IsArchived: false, IsSubAgent: false),
        ];

        Assert.Equal("s", Assert.Single(PaletteSources.Sessions(sessions, "feature", 10)).SessionId);
        Assert.Empty(PaletteSources.Sessions(sessions, "nothing-like-it", 10));
    }

    [Fact]
    public void Sessions_order_by_recent_activity()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("older", "Session one", "forge", "main", At.AddMinutes(-30), IsArchived: false, IsSubAgent: false),
            new("newest", "Session two", "forge", "main", At.AddMinutes(30), IsArchived: false, IsSubAgent: false),
            new("middle", "Session three", "forge", "main", At, IsArchived: false, IsSubAgent: false),
        ];

        Assert.Equal(
            ["newest", "middle", "older"],
            PaletteSources.Sessions(sessions, "session", 10).Select(static item => item.SessionId!).ToArray());
    }

    [Fact]
    public void Sessions_stop_at_the_limit()
    {
        PaletteSessionSnapshot[] sessions =
        [
            new("older", "Session one", "forge", "main", At.AddMinutes(-30), IsArchived: false, IsSubAgent: false),
            new("newest", "Session two", "forge", "main", At.AddMinutes(30), IsArchived: false, IsSubAgent: false),
        ];

        Assert.Equal(
            ["newest"],
            PaletteSources.Sessions(sessions, "session", 1).Select(static item => item.SessionId!).ToArray());
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public void A_command_row_carries_its_category_as_detail_and_its_gesture_twice()
    {
        PaletteCommand[] commands =
        [
            new("palette.files", PaletteStrings.CommandFiles, PaletteStrings.CategoryNavigate, PaletteCommandCatalog.FilesGesture),
        ];

        var items = PaletteSources.Commands(commands, string.Empty, 10);

        var item = Assert.Single(items);
        Assert.Equal(PaletteSource.Commands, item.Source);
        Assert.Equal(PaletteItemKind.Command, item.Kind);
        Assert.Equal(Loc.Instance[PaletteStrings.CommandFiles], item.Title);
        Assert.Equal(Loc.Instance[PaletteStrings.CategoryNavigate], item.Detail);
        Assert.Equal(PaletteCommandCatalog.FilesGesture, item.Badge);
        Assert.Equal(PaletteCommandCatalog.FilesGesture, item.Gesture);
        Assert.True(item.HasGesture);
        Assert.Equal("palette.files", item.CommandId);
    }

    [Fact]
    public void Commands_match_their_localized_category_as_the_secondary_text()
    {
        var git = Loc.Instance[PaletteStrings.CategoryGit];
        var expected = PaletteCommandCatalog.All
            .Where(command => string.Equals(command.Category, git, StringComparison.Ordinal))
            .Select(static command => command.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        var items = PaletteSources.Commands(PaletteCommandCatalog.All, git, PaletteCommandCatalog.All.Count);

        Assert.NotEmpty(expected);
        Assert.Equal(
            expected,
            items.Select(static item => item.CommandId).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        Assert.All(items, item => Assert.Equal(git, item.Detail));
    }

    [Fact]
    public void Commands_of_equal_rank_are_ordered_by_title()
    {
        // The tie-break is only readable in English: in Chinese "打开设置" sorts before "添加项目…", so a test that
        // silently ran on the machine default would pin the opposite order.
        var previous = Loc.Instance.Language;
        Loc.Instance.Language = UiLanguage.English;
        try
        {
            PaletteCommand[] commands =
            [
                new("settings.open", PaletteStrings.CommandOpenSettings, PaletteStrings.CategorySettings, string.Empty),
                new("project.add", PaletteStrings.CommandAddProject, PaletteStrings.CategoryProject, string.Empty),
            ];

            var items = PaletteSources.Commands(commands, string.Empty, 10);
            var titles = items.Select(static item => item.Title).ToArray();

            Assert.Equal(
                [Loc.Instance[PaletteStrings.CommandAddProject], Loc.Instance[PaletteStrings.CommandOpenSettings]],
                titles);
            Assert.Equal(titles.OrderBy(static title => title, StringComparer.OrdinalIgnoreCase).ToArray(), titles);
        }
        finally
        {
            Loc.Instance.Language = previous;
        }
    }

    [Fact]
    public void Commands_stop_at_the_limit()
    {
        var items = PaletteSources.Commands(PaletteCommandCatalog.All, string.Empty, 3);

        Assert.Equal(3, items.Count);
    }

    // ---------------------------------------------------------------- symbols

    [Fact]
    public void A_symbol_row_is_one_based_on_the_document_of_its_snapshot()
    {
        PaletteSymbolSnapshot snapshot = new(
            [
                new PaletteSymbol("Widget", string.Empty, SymbolKind.Class, Line: 4, Column: 6),
                new PaletteSymbol("Draw", "Widget", SymbolKind.Method, Line: 9, Column: 2),
            ],
            "src/Widget.cs",
            null);

        var items = PaletteSources.Symbols(snapshot, string.Empty, 10);

        Assert.Equal(["Widget", "Draw"], items.Select(static item => item.Title).ToArray());
        Assert.Equal([5, 10], items.Select(static item => item.Line).ToArray());
        Assert.Equal([7, 3], items.Select(static item => item.Column).ToArray());
        Assert.All(items, item => Assert.Equal("src/Widget.cs", item.Path));
        Assert.All(items, item => Assert.Equal(PaletteSource.Symbols, item.Source));
        Assert.All(items, item => Assert.Equal(PaletteItemKind.Symbol, item.Kind));

        Assert.Equal(Loc.Instance[PaletteStrings.SymbolKindType], items[0].Badge);
        Assert.Equal(Loc.Instance[PaletteStrings.SymbolKindFunction], items[1].Badge);
        Assert.Equal(string.Empty, items[0].Detail);
        Assert.Equal("Widget", items[1].Detail);
    }

    [Fact]
    public void Symbols_match_their_container_as_the_secondary_text()
    {
        PaletteSymbolSnapshot snapshot = new(
            [
                new PaletteSymbol("Draw", "Widget", SymbolKind.Method, Line: 9, Column: 2),
                new PaletteSymbol("Widget", string.Empty, SymbolKind.Class, Line: 4, Column: 6),
            ],
            "src/Widget.cs",
            null);

        var items = PaletteSources.Symbols(snapshot, "widget", 10);

        Assert.Equal(["Widget", "Draw"], items.Select(static item => item.Title).ToArray());
    }

    [Fact]
    public void Symbols_stop_at_the_limit()
    {
        PaletteSymbolSnapshot snapshot = new(
            [
                new PaletteSymbol("One", string.Empty, SymbolKind.Class, 0, 0),
                new PaletteSymbol("Two", string.Empty, SymbolKind.Class, 1, 0),
            ],
            "src/Widget.cs",
            null);

        Assert.Equal(["One"], PaletteSources.Symbols(snapshot, string.Empty, 1).Select(static item => item.Title).ToArray());
    }

    // ---------------------------------------------------------------- exclusions

    [Theory]
    [InlineData("node_modules/pkg/index.js", true)]
    [InlineData("src/node_modules/pkg/index.js", true)]
    [InlineData("src/NODE_MODULES/pkg/index.js", true)]
    [InlineData("node_modules", true)]
    [InlineData("node_modulesx/pkg/index.js", false)]
    [InlineData("src/xnode_modules/pkg/index.js", false)]
    [InlineData("src/other/index.js", false)]
    public void IsExcluded_matches_a_whole_directory_segment_at_any_depth(string path, bool expected) =>
        Assert.Equal(expected, PaletteSources.IsExcluded(path, ["node_modules"]));

    [Fact]
    public void IsExcluded_is_false_without_a_path_or_an_exclusion()
    {
        Assert.False(PaletteSources.IsExcluded(null, ["node_modules"]));
        Assert.False(PaletteSources.IsExcluded(string.Empty, ["node_modules"]));
        Assert.False(PaletteSources.IsExcluded("node_modules/pkg/index.js", []));
    }

    [Fact]
    public void IsExcluded_matches_any_of_the_excluded_segments()
    {
        string[] excluded = ["bin", ".git"];

        Assert.True(PaletteSources.IsExcluded("src/Bin/tool.dll", excluded));
        Assert.True(PaletteSources.IsExcluded(".git/config", excluded));
        Assert.False(PaletteSources.IsExcluded("src/build/tool.dll", excluded));
    }

    // ---------------------------------------------------------------- symbol badges

    [Theory]
    [InlineData((int)SymbolKind.Class, PaletteStrings.SymbolKindType)]
    [InlineData((int)SymbolKind.Interface, PaletteStrings.SymbolKindType)]
    [InlineData((int)SymbolKind.Namespace, PaletteStrings.SymbolKindType)]
    [InlineData((int)SymbolKind.Method, PaletteStrings.SymbolKindFunction)]
    [InlineData((int)SymbolKind.Constructor, PaletteStrings.SymbolKindFunction)]
    [InlineData((int)SymbolKind.Property, PaletteStrings.SymbolKindMember)]
    [InlineData((int)SymbolKind.EnumMember, PaletteStrings.SymbolKindMember)]
    [InlineData((int)SymbolKind.Variable, PaletteStrings.SymbolKindVariable)]
    [InlineData((int)SymbolKind.Key, PaletteStrings.SymbolKindVariable)]
    [InlineData((int)SymbolKind.String, PaletteStrings.SymbolKindOther)]
    [InlineData((int)SymbolKind.Unknown, PaletteStrings.SymbolKindOther)]
    public void The_symbol_badge_is_the_localized_kind_word(int kind, string expectedKey) =>
        Assert.Equal(Loc.Instance[expectedKey], PaletteSources.SymbolKindBadge((SymbolKind)kind));

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static GrepResult Result(params GrepLine[] lines) =>
        new() { Lines = lines, MatchCount = lines.Count(static line => line.IsMatch) };
}
