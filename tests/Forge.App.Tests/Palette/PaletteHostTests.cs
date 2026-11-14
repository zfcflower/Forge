using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Workbench;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The palette's host side, in <c>MainWindowViewModel.Palette.cs</c>, with the real composition root: the gestures that
/// toggle the overlay slot, the close on a session switch, the session snapshot the store produces (no sub-agent, no
/// soft-deleted session), the navigation a file-backed row triggers, and every command of the catalog reaching the shell's
/// switch. Each test runs against a throw-away data root, like the workbench integration tests.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PaletteHostTests
{
    private const string AlphaFile = "alpha.txt";
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;

    [Fact]
    public async Task Each_gesture_opens_its_source_and_the_same_gesture_again_clears_the_overlay_slot()
    {
        await RunAsync(
            sessionCount: 1,
            deleteLast: false,
            harness =>
            {
                var shell = harness.ViewModel;
                Assert.Null(shell.Palette);

                shell.OpenFilePaletteCommand.Execute(null);
                var palette = Assert.IsType<PaletteViewModel>(shell.Palette);
                Assert.Equal(PaletteSource.Files, palette.Source);

                // Another gesture switches the open palette instead of closing it.
                shell.OpenCommandPaletteCommand.Execute(null);
                Assert.Same(palette, shell.Palette);
                Assert.Equal(PaletteSource.Commands, palette.Source);

                shell.OpenCommandPaletteCommand.Execute(null);
                Assert.Null(shell.Palette);
                Assert.False(palette.IsOpen);

                shell.OpenSearchPaletteCommand.Execute(null);
                Assert.Equal(PaletteSource.Content, Assert.IsType<PaletteViewModel>(shell.Palette).Source);

                // The title-bar button is Ctrl+P.
                shell.OpenSearchPaletteCommand.Execute(null);
                shell.OpenPaletteCommand.Execute(null);
                Assert.Equal(PaletteSource.Files, Assert.IsType<PaletteViewModel>(shell.Palette).Source);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task Switching_the_active_session_closes_an_open_palette()
    {
        await RunAsync(
            sessionCount: 2,
            deleteLast: false,
            harness =>
            {
                var shell = harness.ViewModel;
                shell.OpenFilePaletteCommand.Execute(null);
                Assert.NotNull(shell.Palette);

                shell.Workbench.ActivateTab(
                    Assert.IsType<TabModel>(shell.Workbench.FindTab(WorkbenchIds.SessionTab(harness.SessionIds[^1]))));

                Assert.Equal(harness.SessionIds[^1], shell.ActiveSession?.SessionId);
                Assert.Null(shell.Palette);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task The_session_snapshot_leaves_out_a_soft_deleted_session()
    {
        await RunAsync(
            sessionCount: 3,
            deleteLast: true,
            async harness =>
            {
                IPaletteHost host = harness.ViewModel;

                var sessions = await host.RefreshSessionsAsync(CancellationToken.None);

                Assert.Equal(
                    harness.SessionIds.Take(2).Order(StringComparer.Ordinal),
                    sessions.Select(static session => session.Id).Order(StringComparer.Ordinal));
                Assert.DoesNotContain(sessions, static session => session.IsSubAgent);
                Assert.Same(sessions, host.CachedSessions);

                // The active one is not archived; the others opened as read-only views are.
                Assert.False(sessions.Single(session => session.Id == harness.SessionIds[0]).IsArchived);
                Assert.True(sessions.Single(session => session.Id == harness.SessionIds[1]).IsArchived);
            });
    }

    [Fact]
    public async Task A_content_row_opens_its_file_in_the_preview_tab()
    {
        await RunAsync(
            sessionCount: 1,
            deleteLast: false,
            async harness =>
            {
                IPaletteHost host = harness.ViewModel;
                var item = new PaletteItem
                {
                    Source = PaletteSource.Content,
                    Kind = PaletteItemKind.ContentLine,
                    Title = AlphaFile + ":2",
                    Path = AlphaFile,
                    Line = 2,
                };

                await host.ActivateAsync(item, CancellationToken.None);

                await PumpUntilAsync(() =>
                    harness.ViewModel.Workbench.FindTab(WorkbenchIds.PreviewTab) is { Content: FileDocumentViewModel document } &&
                    document.Path.EndsWith(AlphaFile, StringComparison.OrdinalIgnoreCase));
            });
    }

    [Fact]
    public async Task A_content_search_without_a_workspace_file_match_answers_empty_and_honours_the_budget()
    {
        await RunAsync(
            sessionCount: 1,
            deleteLast: false,
            async harness =>
            {
                IPaletteHost host = harness.ViewModel;

                var hit = await host.SearchContentAsync("second line", 1, CancellationToken.None);
                var miss = await host.SearchContentAsync("no such text anywhere", 10, CancellationToken.None);

                Assert.Single(hit.Lines);
                Assert.True(hit.Truncated || hit.MatchCount >= 1);
                Assert.Empty(miss.Lines);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                    () => host.SearchContentAsync("second line", 0, CancellationToken.None));
            });
    }

    [Fact]
    public async Task Every_catalog_command_reaches_the_shell_and_the_navigation_ones_open_their_source()
    {
        await RunAsync(
            sessionCount: 1,
            deleteLast: false,
            harness =>
            {
                var shell = harness.ViewModel;
                foreach (var command in PaletteCommandCatalog.All)
                {
                    // AddProject opens a folder picker that a headless shell does not have; everything else runs for real.
                    if (command.Id == PaletteCommandIds.AddProject)
                    {
                        continue;
                    }

                    shell.RunPaletteCommand(command.Id);
                    if (shell.Palette is PaletteViewModel open)
                    {
                        open.Close();
                    }
                }

                (string Id, PaletteSource Source)[] navigation =
                [
                    (PaletteCommandIds.Files, PaletteSource.Files),
                    (PaletteCommandIds.Content, PaletteSource.Content),
                    (PaletteCommandIds.Sessions, PaletteSource.Sessions),
                    (PaletteCommandIds.Symbols, PaletteSource.Symbols),
                    (PaletteCommandIds.Commands, PaletteSource.Commands),
                ];
                foreach (var (id, source) in navigation)
                {
                    shell.RunPaletteCommand(id);
                    Assert.Equal(source, Assert.IsType<PaletteViewModel>(shell.Palette).Source);
                    Assert.IsType<PaletteViewModel>(shell.Palette).Close();
                }

                return Task.CompletedTask;
            });
    }

    private static async Task RunAsync(int sessionCount, bool deleteLast, Func<Harness, Task> body)
    {
        await SharedUi.Session.Dispatch(
            async () =>
            {
                var harness = await Harness.CreateAsync(sessionCount, deleteLast);
                try
                {
                    await body(harness);
                }
                finally
                {
                    await harness.DisposeAsync();
                }

                return 0;
            },
            CancellationToken.None);
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        Assert.True(condition(), "the pending palette navigation did not finish");
    }

    /// <summary>A throw-away data root, a real composition root, and the root view model restored over its sessions.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string root;
        private readonly EnvironmentSnapshot previous;

        private Harness(string root, EnvironmentSnapshot previous, AppComposition composition, MainWindowViewModel viewModel, IReadOnlyList<string> sessionIds)
        {
            this.root = root;
            this.previous = previous;
            Composition = composition;
            ViewModel = viewModel;
            SessionIds = sessionIds;
        }

        public AppComposition Composition { get; }

        public MainWindowViewModel ViewModel { get; }

        public IReadOnlyList<string> SessionIds { get; }

        /// <summary>
        /// <paramref name="sessionCount"/> root sessions of one work directory: the first Active, the others archived (one
        /// Active root per workspace), the last one soft-deleted when <paramref name="deleteLast"/> is set. The open-tabs
        /// snapshot restores every session that is not deleted.
        /// </summary>
        public static async Task<Harness> CreateAsync(int sessionCount, bool deleteLast)
        {
            var (root, previous) = TempDataRoot.Redirect("forge-palette-", "work");
            var work = Path.Combine(root, "work");
            File.WriteAllLines(Path.Combine(work, AlphaFile), ["first line", "second line", "third line"]);

            var composition = AppComposition.Create();
            var catalog = composition.Catalog;
            var sessionIds = new List<string>(sessionCount);
            for (var i = 0; i < sessionCount; i++)
            {
                var info = await Task.Run(() => composition.Sessions.CreateSessionAsync(work, "session " + i, CancellationToken.None));
                await Task.Run(() => catalog.SetStatusAsync(info.Id, SessionStatus.Archived, CancellationToken.None));
                sessionIds.Add(info.Id);
            }

            var firstId = sessionIds[0];
            Assert.True(await Task.Run(() => catalog.ActivateAsync(firstId, CancellationToken.None)));
            var restored = sessionIds;
            if (deleteLast)
            {
                var lastId = sessionIds[^1];
                Assert.True(await Task.Run(() => catalog.SetStatusAsync(lastId, SessionStatus.Deleted, CancellationToken.None)));
                restored = sessionIds[..^1];
            }

            var tabs = new OpenTabsSnapshot { Sessions = restored, ActiveSession = firstId };
            var tabsJson = JsonSerializer.Serialize(tabs, WorkbenchJsonContext.Default.OpenTabsSnapshot);
            await Task.Run(() => composition.UiState.PutAsync("open-tabs", OpenTabsSnapshot.CurrentVersion, tabsJson, CancellationToken.None));

            var viewModel = new MainWindowViewModel(composition);
            await viewModel.InitializeAsync();
            return new Harness(root, previous, composition, viewModel, sessionIds);
        }

        public async ValueTask DisposeAsync()
        {
            ViewModel.Files.Attach(PanelContext.Empty);
            ViewModel.Dispose();
            await Composition.DisposeAsync();
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }
}
