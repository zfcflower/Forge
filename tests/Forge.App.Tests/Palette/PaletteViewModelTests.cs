using System.Collections.Concurrent;
using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Editor.Lsp;
using Forge.IO;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The palette's state machine, driven through its public surface with a recording host: what one keystroke does (a
/// rebuild from the snapshot in hand, a debounced search, a cancelled earlier one), what a failure leaves behind (a
/// status line and, when nothing else is left to show, one notice row), what Enter does to an open palette, and what the
/// preview beside the list says. Nothing here waits on a wall clock without a bound: the debounce is awaited with a
/// polling budget, and the content answers are handed over through gates the test opens itself.
/// </summary>
public sealed class PaletteViewModelTests
{
    private const int Bound = 200;

    // ---------------------------------------------------------------- opening

    [Fact]
    public async Task Opening_shows_the_cached_snapshot_before_the_refresh_answers_and_starts_it_once()
    {
        var host = new FakePaletteHost { CachedFiles = ["src/Main.cs", "src/Other.cs"] };
        host.HoldFilesRefresh();
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Files);

        Assert.True(viewModel.IsOpen);
        Assert.Equal(PaletteSource.Files, viewModel.Source);
        Assert.Equal(string.Empty, viewModel.Query);
        Assert.Equal(["Main.cs", "Other.cs"], Titles(viewModel));
        Assert.Same(viewModel.Items[0], viewModel.SelectedItem);
        Assert.Equal(1, host.FilesRefreshes);

        // The answer of the refresh the opening started replaces the cached rows, so the cached ones really were the
        // only thing shown until it landed.
        host.ReleaseFilesRefresh(["docs/readme.md"]);
        Assert.True(await TestPump.UntilAsync(() => host.FilesRefreshes == 1 && viewModel.Items.Count == 1));
        Assert.Equal(["readme.md"], Titles(viewModel));
    }

    [Fact]
    public void Opening_again_clears_the_query_of_the_previous_open()
    {
        using var viewModel = new PaletteViewModel(HeldFiles("src/Main.cs", "src/Other.cs"), new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        viewModel.Query = "main";
        Assert.Equal(["Main.cs"], Titles(viewModel));

        viewModel.Close();
        viewModel.Open(PaletteSource.Files);

        Assert.Equal(string.Empty, viewModel.Query);
        Assert.Equal(["Main.cs", "Other.cs"], Titles(viewModel));
    }

    [Fact]
    public void Typing_filters_the_rows_and_selects_the_first_one_synchronously()
    {
        var host = HeldFiles("src/Main.cs", "src/Other.cs", "docs/readme.md");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);

        viewModel.Query = "main";

        Assert.Equal(["Main.cs"], Titles(viewModel));
        Assert.Equal(viewModel.Items[0], viewModel.SelectedItem);
        Assert.Equal(Loc.Instance[PaletteStrings.PlaceholderFiles], viewModel.Placeholder);
        Assert.Equal(Loc.Instance[PaletteStrings.EmptyFiles], viewModel.EmptyText);
    }

    [Theory]
    [InlineData('>', (int)PaletteSource.Commands, PaletteStrings.PlaceholderCommands, PaletteStrings.EmptyCommands)]
    [InlineData('#', (int)PaletteSource.Content, PaletteStrings.PlaceholderContent, PaletteStrings.EmptyContent)]
    [InlineData(':', (int)PaletteSource.Sessions, PaletteStrings.PlaceholderSessions, PaletteStrings.EmptySessions)]
    [InlineData('@', (int)PaletteSource.Symbols, PaletteStrings.PlaceholderSymbols, PaletteStrings.EmptySymbols)]
    public async Task A_leading_prefix_switches_the_source(char prefix, int sourceKind, string placeholder, string empty)
    {
        // The source enum is internal, so the theory hands over its value instead of the type.
        var expected = (PaletteSource)sourceKind;
        using var viewModel = new PaletteViewModel(Files("src/Main.cs"), new AppPreferences());
        viewModel.Open(PaletteSource.Files);

        viewModel.Query = prefix + "main";

        Assert.Equal(expected, viewModel.Source);
        Assert.Equal(prefix + "main", viewModel.Query);
        Assert.Equal(Loc.Instance[placeholder], viewModel.Placeholder);
        Assert.Equal(Loc.Instance[viewModel.Sources.Single(option => option.Source == expected).LabelKey], viewModel.HeaderText);

        // The empty word waits for the switched source's first snapshot: until it lands the list says it is reading.
        var expectedEmpty = Loc.Instance[empty];
        Assert.True(
            await TestPump.UntilAsync(() => viewModel.EmptyText == expectedEmpty, Bound),
            $"the empty text stayed '{viewModel.EmptyText}' instead of '{expectedEmpty}'");
    }

    [Fact]
    public async Task The_empty_word_says_reading_until_the_first_snapshot_of_a_source_lands()
    {
        // No cached files and a held refresh: nothing is known about the workspace yet, so the list must not claim it
        // holds no match for the query.
        var host = new FakePaletteHost();
        host.HoldFilesRefresh();
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Files);

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(Loc.Instance[PaletteStrings.StatusLoading], viewModel.EmptyText);

        host.ReleaseFilesRefresh([]);
        var expected = Loc.Instance[PaletteStrings.EmptyFiles];
        Assert.True(
            await TestPump.UntilAsync(() => viewModel.EmptyText == expected, Bound),
            $"the empty text stayed '{viewModel.EmptyText}' instead of '{expected}'");
    }

    [Fact]
    public void A_prefix_that_is_not_the_first_character_is_only_a_query()
    {
        using var viewModel = new PaletteViewModel(Files("src/Main.cs", "a>b.cs"), new AppPreferences());
        viewModel.Open(PaletteSource.Files);

        viewModel.Query = "a>b";

        Assert.Equal(PaletteSource.Files, viewModel.Source);
        Assert.Equal(["a>b.cs"], Titles(viewModel));
    }

    [Fact]
    public void Choosing_a_source_strips_the_typed_prefix_from_the_query()
    {
        using var viewModel = new PaletteViewModel(Files("src/Main.cs"), new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        viewModel.Query = ">command words";
        Assert.Equal(PaletteSource.Commands, viewModel.Source);

        viewModel.SelectedSource = viewModel.Sources.Single(option => option.Source == PaletteSource.Sessions);

        Assert.Equal(PaletteSource.Sessions, viewModel.Source);
        Assert.Equal("command words", viewModel.Query);
        Assert.Equal(Loc.Instance[PaletteStrings.SourceSessions], viewModel.HeaderText);
    }

    // ---------------------------------------------------------------- moving and accepting

    [Fact]
    public void Moving_the_selection_clamps_at_both_ends()
    {
        using var viewModel = new PaletteViewModel(HeldFiles("a.cs", "b.cs", "c.cs"), new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Titles(viewModel));

        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[0], viewModel.SelectedItem);

        viewModel.MoveSelection(1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);

        viewModel.MoveSelection(1);
        viewModel.MoveSelection(1);
        Assert.Same(viewModel.Items[2], viewModel.SelectedItem);

        viewModel.MoveSelection(-1);
        Assert.Same(viewModel.Items[1], viewModel.SelectedItem);
    }

    [Fact]
    public void Accept_closes_the_palette_before_it_activates_the_row()
    {
        var host = HeldFiles("src/Main.cs");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        host.ObserveOpen = () => viewModel.IsOpen;
        viewModel.Open(PaletteSource.Files);
        var row = Assert.Single(viewModel.Items);

        viewModel.Accept();

        var activation = Assert.Single(host.Activations);
        Assert.Same(row, activation.Item);
        Assert.False(activation.WasOpen, "the palette has to be closed before the host is called");
        Assert.False(viewModel.IsOpen);
        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.SelectedItem);
    }

    [Fact]
    public void Accept_on_a_notice_row_does_nothing_and_leaves_the_palette_open()
    {
        var host = HeldFiles();
        host.Workspace = null;
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Null(viewModel.SelectedItem);

        viewModel.SelectedItem = notice;
        viewModel.Accept();

        Assert.Empty(host.Activations);
        Assert.True(viewModel.IsOpen);
        Assert.Equal(Loc.Instance[PaletteStrings.PreviewNotice], viewModel.PreviewTitle);
        Assert.Equal(notice.Title, viewModel.PreviewText);
    }

    // ---------------------------------------------------------------- content source

    [Fact]
    public async Task A_content_search_starts_only_after_the_quiet_period()
    {
        var host = new FakePaletteHost();
        host.Answer("alpha", Found(Line("src/a.cs", 3, "alpha")));
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Content);
        Assert.Empty(host.Searches);

        var clock = Stopwatch.StartNew();
        viewModel.Query = "alpha";

        Assert.Empty(host.Searches);
        Assert.True(viewModel.IsSearching);
        Assert.Equal(Loc.Instance[PaletteStrings.StatusSearching], viewModel.StatusText);
        Assert.True(await TestPump.UntilAsync(() => host.Searches.Count == 1, Bound), "the debounced search never started");
        clock.Stop();
        Assert.True(
            clock.Elapsed >= TimeSpan.FromMilliseconds(150),
            $"the search started after {clock.ElapsedMilliseconds} ms, so the quiet period is not in effect");
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1, Bound));
        Assert.Equal(["src/a.cs:3"], Titles(viewModel));
        Assert.False(viewModel.IsSearching);
        Assert.Equal(host.WorkspaceLabel, viewModel.StatusText);
    }

    [Fact]
    public async Task Typing_on_cancels_the_search_in_flight_and_only_the_last_answer_becomes_rows()
    {
        var host = new FakePaletteHost();
        host.HoldSearch("alpha");
        host.Answer("beta", Found(Line("src/b.cs", 1, "beta")));
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Content);
        viewModel.Query = "alpha";
        Assert.True(await TestPump.UntilAsync(() => host.Searches.Count == 1, Bound), "the first search never started");

        viewModel.Query = "beta";

        Assert.True(await TestPump.UntilAsync(() => host.Searches.Count == 2, Bound), "the second search never started");
        Assert.Equal(["alpha", "beta"], host.Searches.Select(static search => search.Query));
        Assert.True(host.Searches[0].IsCancelled, "the search of the abandoned query has to be cancelled");
        Assert.False(host.Searches[1].IsCancelled);
        Assert.Equal(1, host.SearchCancellations);
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1, Bound));
        Assert.Equal(["src/b.cs:1"], Titles(viewModel));

        // The abandoned answer arrives late; it must not become the list.
        host.ReleaseSearch("alpha", Found(Line("src/a.cs", 1, "alpha"), Line("src/a.cs", 2, "alpha")));
        Assert.Equal(["src/b.cs:1"], Titles(viewModel));
        Assert.False(await TestPump.UntilAsync(() => viewModel.Items.Count != 1, 20));
    }

    [Fact]
    public async Task An_empty_content_query_searches_nothing_and_clears_the_list()
    {
        var host = new FakePaletteHost();
        host.Answer("alpha", Found(Line("src/a.cs", 3, "alpha")));
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Content);
        viewModel.Query = "alpha";
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1, Bound));

        viewModel.Query = string.Empty;

        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsSearching);
        Assert.Equal(Loc.Instance[PaletteStrings.EmptyContentQuery], viewModel.EmptyText);
        Assert.Single(host.Searches);
        Assert.False(await TestPump.UntilAsync(() => host.Searches.Count > 1, 60), "an empty query must never search");
    }

    [Fact]
    public async Task A_truncated_content_result_says_so_in_the_status_line()
    {
        var preferences = new AppPreferences();
        var host = new FakePaletteHost();
        host.Answer("alpha", Found(Line("src/a.cs", 3, "alpha")) with { Truncated = true });
        using var viewModel = new PaletteViewModel(host, preferences);
        viewModel.Open(PaletteSource.Content);

        viewModel.Query = "alpha";

        Assert.True(await TestPump.UntilAsync(
            () => viewModel.StatusText == Loc.Instance.Format(
                PaletteStrings.StatusTruncated, TextFormat.Number(preferences.PaletteResultLimit)),
            Bound));
        Assert.Single(viewModel.Items);
    }

    [Fact]
    public async Task Rows_under_an_excluded_directory_are_dropped()
    {
        var preferences = new AppPreferences { PaletteExcludeDirectories = "node_modules" };
        var host = new FakePaletteHost();
        host.Answer(
            "alpha",
            Found(Line("node_modules/pkg/index.js", 4, "alpha"), Line("src/a.cs", 3, "alpha")));
        using var viewModel = new PaletteViewModel(host, preferences);
        viewModel.Open(PaletteSource.Content);

        viewModel.Query = "alpha";

        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1, Bound));
        Assert.Equal(["src/a.cs:3"], Titles(viewModel));
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task A_failed_file_refresh_becomes_the_status_line_and_never_escapes()
    {
        var host = new FakePaletteHost { FilesFailure = new IOException("no disk") };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Files);

        var failure = Loc.Instance.Format(PaletteStrings.NoticeFilesFailed, "no disk");
        Assert.True(await TestPump.UntilAsync(() => viewModel.StatusText == failure, Bound));
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsSearching);
    }

    [Fact]
    public async Task A_failed_session_refresh_leaves_one_notice_row_and_its_status_line()
    {
        var host = new FakePaletteHost { SessionsFailure = new IOException("no store") };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Sessions);

        var failure = Loc.Instance.Format(PaletteStrings.NoticeSessionsFailed, "no store");
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1 && viewModel.StatusText == failure, Bound));
        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Equal(failure, notice.Title);
        Assert.Null(viewModel.SelectedItem);
        Assert.Equal(1, host.SessionsRefreshes);
    }

    [Fact]
    public async Task A_failed_symbols_refresh_is_the_notice_of_the_symbol_list()
    {
        var snapshot = new PaletteSymbolSnapshot([], "Program.cs", null);
        var host = new FakePaletteHost { CachedSymbols = snapshot, SymbolsFailure = new IOException("no server") };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Symbols);

        var failure = Loc.Instance[PaletteStrings.NoticeSymbolsUnavailable];
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1 && viewModel.StatusText == failure, Bound));
        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Equal(failure, notice.Title);
        Assert.Equal(1, host.SymbolsRefreshes);
    }

    [Fact]
    public async Task A_failed_search_becomes_the_notice_of_the_content_list()
    {
        var host = new FakePaletteHost { SearchFailure = new IOException("gone") };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Content);

        viewModel.Query = "alpha";

        var failure = Loc.Instance.Format(PaletteStrings.NoticeSearchFailed, "gone");
        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count == 1, Bound));
        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Equal(failure, notice.Title);
        Assert.False(viewModel.IsSearching);
    }

    // ---------------------------------------------------------------- the configured limit

    [Theory]
    [InlineData((int)PaletteSource.Files)]
    [InlineData((int)PaletteSource.Content)]
    [InlineData((int)PaletteSource.Sessions)]
    [InlineData((int)PaletteSource.Commands)]
    [InlineData((int)PaletteSource.Symbols)]
    public async Task The_configured_result_limit_bounds_every_source(int sourceKind)
    {
        // The source enum is internal, so the theory hands over its value instead of the type.
        var source = (PaletteSource)sourceKind;
        var preferences = new AppPreferences { PaletteResultLimit = AppPreferences.MinPaletteResultLimit };
        var host = ArrangeTwelve(source);
        using var viewModel = new PaletteViewModel(host, preferences);

        viewModel.Open(source);
        if (source == PaletteSource.Content)
        {
            viewModel.Query = "hit";
        }

        Assert.True(await TestPump.UntilAsync(() => viewModel.Items.Count > 0, Bound));
        Assert.Equal(AppPreferences.MinPaletteResultLimit, viewModel.Items.Count);
        Assert.True(viewModel.HasItems);
        Assert.False(viewModel.IsEmpty);
    }

    // ---------------------------------------------------------------- symbols

    [Fact]
    public void Symbols_without_an_editor_document_show_the_no_document_notice()
    {
        using var viewModel = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());

        viewModel.Open(PaletteSource.Symbols);

        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Equal(Loc.Instance[PaletteStrings.NoticeNoDocument], notice.Title);
        Assert.Null(viewModel.SelectedItem);
    }

    [Fact]
    public void A_symbol_snapshot_problem_is_the_notice_of_the_symbol_list()
    {
        var snapshot = new PaletteSymbolSnapshot([], "Program.cs", "no language server");
        var host = new FakePaletteHost { CachedSymbols = snapshot, SymbolsAnswer = snapshot };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Symbols);

        var notice = Assert.Single(viewModel.Items);
        Assert.True(notice.IsNotice);
        Assert.Equal("no language server", notice.Title);
    }

    [Fact]
    public void A_symbol_row_reveals_the_one_based_line_and_column_of_the_snapshot()
    {
        var snapshot = new PaletteSymbolSnapshot(
            [new PaletteSymbol("Run", "Program", SymbolKind.Method, Line: 4, Column: 2)],
            "Program.cs",
            null);
        var host = new FakePaletteHost { CachedSymbols = snapshot, SymbolsAnswer = snapshot };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Symbols);

        var row = Assert.Single(viewModel.Items);
        Assert.Equal(1, host.SymbolsRefreshes);
        Assert.Equal(PaletteItemKind.Symbol, row.Kind);
        Assert.Equal(("Run", "Program"), (row.Title, row.Detail));
        Assert.Equal((5, 3), (row.Line, row.Column));
        Assert.Equal("Program.cs", row.Path);
        Assert.Equal(Loc.Instance[PaletteStrings.SymbolKindFunction], row.Badge);
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public void A_command_row_previews_its_category_and_gesture_without_reading_anything()
    {
        using var viewModel = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());
        viewModel.Open(PaletteSource.Commands);
        var row = viewModel.SelectedItem;
        Assert.NotNull(row);

        Assert.Equal(PaletteItemKind.Command, row!.Kind);
        Assert.Equal(Loc.Instance[PaletteStrings.PreviewCommand], viewModel.PreviewTitle);
        Assert.Equal(row.Detail + "\n" + row.Gesture, viewModel.PreviewText);
        Assert.False(viewModel.IsPreviewLoading);
    }

    [Fact]
    public void A_session_row_previews_its_project_branch_and_archived_badge()
    {
        var session = new PaletteSessionSnapshot(
            "s1", "Fix the palette", "forge", "main", DateTimeOffset.UnixEpoch, IsArchived: true, IsSubAgent: false);
        var host = new FakePaletteHost { CachedSessions = [session], SessionsAnswer = [session] };
        using var viewModel = new PaletteViewModel(host, new AppPreferences());

        viewModel.Open(PaletteSource.Sessions);

        var row = Assert.Single(viewModel.Items);
        Assert.Equal(Loc.Instance[PaletteStrings.PreviewSession], viewModel.PreviewTitle);
        Assert.Equal(
            row.Title + "\n" + row.Detail + "\n" + Loc.Instance[PaletteStrings.SessionArchived],
            viewModel.PreviewText);
        Assert.False(viewModel.IsPreviewLoading);
    }

    [Fact]
    public async Task A_file_row_loads_its_text_asynchronously_with_a_loading_flag()
    {
        var root = TestTempFiles.NewDirectory("forge-palette-preview-");
        try
        {
            TestTempFiles.WriteFile(root, "notes.txt", "alpha line\nbeta line\n");
            var host = new FakePaletteHost { Workspace = root, CachedFiles = ["notes.txt"], FilesAnswer = ["notes.txt"] };
            using var viewModel = new PaletteViewModel(host, new AppPreferences());
            var loading = new ConcurrentQueue<bool>();
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PaletteViewModel.IsPreviewLoading))
                {
                    loading.Enqueue(viewModel.IsPreviewLoading);
                }
            };

            viewModel.Open(PaletteSource.Files);

            Assert.Equal(Loc.Instance[PaletteStrings.PreviewFile], viewModel.PreviewTitle);
            Assert.True(await TestPump.UntilAsync(() => !viewModel.IsPreviewLoading && viewModel.PreviewText.Length > 0, Bound));
            Assert.Equal([true, false], loading.ToArray());
            Assert.Contains("alpha line", viewModel.PreviewText, StringComparison.Ordinal);
            Assert.True(viewModel.HasPreview);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Fact]
    public async Task A_row_pointing_at_a_missing_file_ends_with_the_nothing_to_preview_text()
    {
        var root = TestTempFiles.NewDirectory("forge-palette-missing-");
        try
        {
            var host = new FakePaletteHost { Workspace = root, CachedFiles = ["gone.txt"], FilesAnswer = ["gone.txt"] };
            using var viewModel = new PaletteViewModel(host, new AppPreferences());

            viewModel.Open(PaletteSource.Files);

            Assert.True(await TestPump.UntilAsync(() => !viewModel.IsPreviewLoading, Bound));
            Assert.Equal(Loc.Instance[PaletteStrings.PreviewEmpty], viewModel.PreviewText);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    // ---------------------------------------------------------------- closing and disposing

    [Fact]
    public async Task Close_cancels_the_refresh_it_started()
    {
        var host = HeldFiles("src/Main.cs");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Files);
        Assert.Single(viewModel.Items);

        viewModel.Close();

        Assert.False(viewModel.IsOpen);
        Assert.True(await TestPump.UntilAsync(() => host.FilesCancellations == 1, Bound), "the in-flight refresh was not cancelled");
        Assert.Empty(viewModel.Items);
    }

    [Fact]
    public async Task Close_cancels_the_search_it_started()
    {
        var host = new FakePaletteHost();
        host.HoldSearch("alpha");
        using var viewModel = new PaletteViewModel(host, new AppPreferences());
        viewModel.Open(PaletteSource.Content);
        viewModel.Query = "alpha";
        Assert.True(await TestPump.UntilAsync(() => host.Searches.Count == 1, Bound));

        viewModel.Close();
        host.ReleaseSearch("alpha", Found(Line("src/a.cs", 1, "alpha")));

        Assert.False(viewModel.IsOpen);
        Assert.True(await TestPump.UntilAsync(() => host.SearchCancellations == 1, Bound), "the in-flight search was not cancelled");
        Assert.Empty(viewModel.Items);
        Assert.False(await TestPump.UntilAsync(() => viewModel.Items.Count > 0, 20));
    }

    [Fact]
    public void Close_clears_the_rows_the_selection_the_preview_and_the_search_state()
    {
        using var viewModel = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());
        viewModel.Open(PaletteSource.Commands);
        Assert.NotEmpty(Titles(viewModel));
        Assert.NotNull(viewModel.SelectedItem);
        Assert.NotEqual(string.Empty, viewModel.PreviewText);

        viewModel.Close();

        Assert.False(viewModel.IsOpen);
        Assert.Empty(viewModel.Items);
        Assert.Null(viewModel.SelectedItem);
        Assert.False(viewModel.HasSelectedItem);
        Assert.False(viewModel.IsSearching);
        Assert.False(viewModel.IsPreviewLoading);
        Assert.Equal(string.Empty, viewModel.PreviewTitle);
        Assert.Equal(string.Empty, viewModel.PreviewText);
        Assert.False(viewModel.HasPreview);
    }

    [Fact]
    public void Dispose_is_idempotent_and_a_later_language_switch_rebuilds_nothing()
    {
        using var language = new LanguageScope(UiLanguage.English);
        var viewModel = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());
        viewModel.Open(PaletteSource.Commands);
        Assert.NotEmpty(viewModel.Items);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.Dispose();
        viewModel.Dispose();
        raised.Clear();

        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.True(viewModel.IsDisposed);
        Assert.Empty(raised);
    }

    // ---------------------------------------------------------------- language

    [Fact]
    public void A_language_switch_re_renders_the_texts_and_the_command_rows()
    {
        using var language = new LanguageScope(UiLanguage.English);
        using var viewModel = new PaletteViewModel(new FakePaletteHost(), new AppPreferences());
        viewModel.Open(PaletteSource.Commands);
        var english = (viewModel.Placeholder, viewModel.HeaderText, viewModel.EmptyText, Titles(viewModel));
        Assert.Equal(Loc.Instance[PaletteStrings.PlaceholderCommands], english.Placeholder);
        Assert.Equal(CommandTitles(), english.Item4);

        Loc.Instance.Language = UiLanguage.Chinese;

        Assert.Equal(Loc.Instance[PaletteStrings.PlaceholderCommands], viewModel.Placeholder);
        Assert.Equal(Loc.Instance[PaletteStrings.SourceCommands], viewModel.HeaderText);
        Assert.Equal(Loc.Instance[PaletteStrings.EmptyCommands], viewModel.EmptyText);
        Assert.Equal(CommandTitles(), Titles(viewModel));
        Assert.NotEqual(english.Placeholder, viewModel.Placeholder);
        Assert.NotEqual(english.HeaderText, viewModel.HeaderText);
        Assert.NotEqual(english.Item3, viewModel.EmptyText);
        Assert.NotEqual(english.Item4, Titles(viewModel));
    }

    // ---------------------------------------------------------------- fixtures

    private static FakePaletteHost Files(params string[] files) => new() { CachedFiles = files, FilesAnswer = files };

    /// <summary>
    /// A file host whose refresh never answers, so the rows and the selected instance stay exactly what the cached
    /// snapshot produced; a landing refresh rebuilds the list, and a test that asserts on one row's identity or moves the
    /// selection would then be racing it.
    /// </summary>
    private static FakePaletteHost HeldFiles(params string[] files)
    {
        var host = Files(files);
        host.HoldFilesRefresh();
        return host;
    }

    private static string[] Titles(PaletteViewModel viewModel) => [.. viewModel.Items.Select(static item => item.Title)];

    private static string[] CommandTitles() =>
        [.. PaletteCommandCatalog.All.Select(static command => command.Title).OrderBy(static title => title, StringComparer.OrdinalIgnoreCase)];

    private static GrepLine Line(string path, int number, string text) => new(path, number, text, IsMatch: true);

    private static GrepResult Found(params GrepLine[] lines) =>
        new() { Lines = lines, MatchCount = lines.Count(static line => line.IsMatch) };

    /// <summary>Twelve rows for one source, so a limit below twelve has something to cut.</summary>
    private static FakePaletteHost ArrangeTwelve(PaletteSource source)
    {
        const int Twelve = 12;
        switch (source)
        {
            case PaletteSource.Files:
                var files = Enumerable.Range(0, Twelve).Select(index => "src/f" + index + ".cs").ToArray();
                return new FakePaletteHost { CachedFiles = files, FilesAnswer = files };
            case PaletteSource.Content:
                var host = new FakePaletteHost();
                host.Answer(
                    "hit",
                    Found([.. Enumerable.Range(1, Twelve).Select(number => Line("src/a.cs", number, "hit"))]));
                return host;
            case PaletteSource.Sessions:
                var sessions = Enumerable.Range(0, Twelve)
                    .Select(index => new PaletteSessionSnapshot(
                        "s" + index, "Session " + index, "forge", "main", DateTimeOffset.UnixEpoch, false, false))
                    .ToArray();
                return new FakePaletteHost { CachedSessions = sessions, SessionsAnswer = sessions };
            case PaletteSource.Symbols:
                var symbols = new PaletteSymbolSnapshot(
                    [.. Enumerable.Range(0, Twelve).Select(index => new PaletteSymbol("Symbol" + index, "Type", SymbolKind.Method, index, 0))],
                    "Program.cs",
                    null);
                return new FakePaletteHost { CachedSymbols = symbols, SymbolsAnswer = symbols };
            case PaletteSource.Commands:
                // The command catalog is static data and holds more entries than the smallest limit allows.
                return new FakePaletteHost();
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "no twelve-row fixture for this source");
        }
    }
}
