using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Headless;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins "the diff and the spec tree are built on a pool thread and land in one Reset" (forge-app spec, "变更" and
/// "规格" panels): a large spec graph is read and flattened off the dispatcher, both panels swap their whole virtualized
/// row list in a single Reset instead of one notification per row, a refresh does not lose the nodes the user collapsed,
/// and a large working-tree diff still arrives completely.
///
/// Thread affinity is pinned on the spec tree: its graph factory is the only injectable seam and runs exactly where the
/// rows are built, so it records <see cref="Dispatcher.UIThread.CheckAccess"/>. <see cref="ChangesViewModel"/> takes the
/// sealed <see cref="GitCli"/> instead, which leaves no double to observe the diff build from; the diff side is therefore
/// covered by the row-Reset rule only.
/// </summary>
[Collection("forge-app-ui")]
public sealed class BackgroundBuildTests
{
    private const int SpecCount = 220;
    private const int DiffFileCount = 120;
    private const int DiffLinesPerFile = 200;
    private const string SessionId = "session";
    private const string RootSpecId = "spec-0";

    /// <summary>Children of the parent spec; the rest hang one level deeper, so the tree has two depths.</summary>
    private const int RootChildCount = 20;

    [Fact]
    public async Task A_large_spec_graph_is_read_off_the_ui_thread()
    {
        var accesses = await BuildSpecTreeAsync();

        Assert.NotEmpty(accesses);
        Assert.DoesNotContain(true, accesses);
    }

    [Fact]
    public async Task A_spec_tree_refresh_replaces_its_rows_with_a_single_reset()
    {
        var session = SharedUi.Session;
        var resets = await session.Dispatch(
            async () =>
            {
                var root = NewSpecRoot();
                try
                {
                    var panel = new SpecTreeViewModel(static path => new SpecGraph(path), static _ => { });
                    panel.Attach(new PanelContext(SessionId, root, root));
                    Assert.True(await PumpUntilAsync(() => panel.Rows.Count > 0), "the spec tree never finished its first build");

                    var count = 0;
                    panel.Rows.CollectionChanged += (_, args) =>
                    {
                        if (args.Action == NotifyCollectionChangedAction.Reset)
                        {
                            count++;
                        }
                    };

                    await panel.RefreshAsync(CancellationToken.None);
                    return count;
                }
                finally
                {
                    TestTempFiles.DeleteTree(root);
                }
            },
            CancellationToken.None);

        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task Collapsed_spec_nodes_stay_collapsed_after_a_refresh()
    {
        var session = SharedUi.Session;
        var rows = await session.Dispatch(
            async () =>
            {
                var root = NewSpecRoot();
                try
                {
                    var panel = new SpecTreeViewModel(static path => new SpecGraph(path), static _ => { });
                    panel.Attach(new PanelContext(SessionId, root, root));
                    Assert.True(await PumpUntilAsync(() => panel.Rows.Count == SpecCount), "the spec tree never finished its first build");

                    var rootRow = Assert.Single(panel.Rows, static row => row.Id == RootSpecId);
                    rootRow.IsExpanded = false;
                    var collapsed = panel.Rows.Count;

                    await panel.RefreshAsync(CancellationToken.None);
                    return (Collapsed: collapsed, AfterRefresh: panel.Rows.Count, Ids: panel.Rows.Select(static row => row.Id).ToList());
                }
                finally
                {
                    TestTempFiles.DeleteTree(root);
                }
            },
            CancellationToken.None);

        Assert.Equal(1, rows.Collapsed);
        Assert.Equal(rows.Collapsed, rows.AfterRefresh);
        Assert.Equal([RootSpecId], rows.Ids);
    }

    [Fact]
    public async Task A_large_working_tree_diff_replaces_its_files_with_a_single_reset()
    {
        var root = await NewDiffRepositoryAsync();
        try
        {
            var session = SharedUi.Session;
            var outcome = await session.Dispatch(
                async () =>
                {
                    var changes = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
                    changes.Attach(new PanelContext(SessionId, root, root));
                    Assert.True(await PumpUntilAsync(() => changes.Files.Count > 0), "the diff never finished its first load");

                    var resets = 0;
                    changes.Files.CollectionChanged += (_, args) =>
                    {
                        if (args.Action == NotifyCollectionChangedAction.Reset)
                        {
                            resets++;
                        }
                    };

                    await changes.LoadAsync(CancellationToken.None);
                    return (Resets: resets, Files: changes.Files.Count, Blocks: changes.Files.Sum(static file => file.Blocks.Count));
                },
                CancellationToken.None);

            Assert.Equal(1, outcome.Resets);
            Assert.Equal(DiffFileCount, outcome.Files);
            Assert.True(outcome.Blocks >= DiffFileCount, "every changed file must carry at least one change block");
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    /// <summary>Runs one spec-tree build and returns what the graph factory saw: <c>true</c> means "on the UI thread".</summary>
    private static async Task<List<bool>> BuildSpecTreeAsync()
    {
        var session = SharedUi.Session;
        return await session.Dispatch(
            async () =>
            {
                var root = NewSpecRoot();
                try
                {
                    var accesses = new List<bool>();
                    var panel = new SpecTreeViewModel(
                        path =>
                        {
                            lock (accesses)
                            {
                                accesses.Add(Dispatcher.UIThread.CheckAccess());
                            }

                            return new SpecGraph(path);
                        },
                        static _ => { });
                    panel.Attach(new PanelContext(SessionId, root, root));
                    Assert.True(await PumpUntilAsync(() => panel.Rows.Count == SpecCount), "the spec tree never finished its first build");
                    lock (accesses)
                    {
                        return new List<bool>(accesses);
                    }
                }
                finally
                {
                    TestTempFiles.DeleteTree(root);
                }
            },
            CancellationToken.None);
    }

    /// <summary>One parent spec plus <see cref="SpecCount"/> - 1 children, spread over two levels of depth.</summary>
    private static string NewSpecRoot()
    {
        var root = TestTempFiles.NewDirectory("forge-spec-tree-");
        for (var index = 0; index < SpecCount; index++)
        {
            var id = "spec-" + index.ToString(CultureInfo.InvariantCulture);
            var parent = index switch
            {
                0 => null,
                <= RootChildCount => RootSpecId,
                _ => "spec-" + ((index % RootChildCount) + 1).ToString(CultureInfo.InvariantCulture),
            };
            var body = new List<string>
            {
                "---",
                "id: " + id,
                "type: module-design",
                "title: " + id,
                "status: active",
            };
            if (parent is not null)
            {
                body.Add("parent: " + parent);
            }

            body.Add("---");
            body.Add(string.Empty);
            body.Add("# " + id);
            File.WriteAllLines(Path.Combine(root, id + ".md"), body);
        }

        return root;
    }

    /// <summary>A repository whose working tree has one committed file per diff target, all of them modified again.</summary>
    private static async Task<string> NewDiffRepositoryAsync()
    {
        var root = TestTempFiles.NewDirectory("forge-diff-");

        var git = new GitCli();
        foreach (var arguments in new[] { new[] { "init" }, ["config", "user.email", "forge@example.invalid"], ["config", "user.name", "Forge Tests"] })
        {
            var result = await git.RunAsync(root, arguments, CancellationToken.None);
            Assert.True(result.Success, "git " + arguments[0] + " failed: " + result.StandardError);
        }

        for (var file = 0; file < DiffFileCount; file++)
        {
            await File.WriteAllLinesAsync(Path.Combine(root, FileName(file)), Lines(file, "committed"));
        }

        await git.CommitAsync(root, [], "initial", CancellationToken.None);

        for (var file = 0; file < DiffFileCount; file++)
        {
            await File.WriteAllLinesAsync(Path.Combine(root, FileName(file)), Lines(file, "changed"));
        }

        return root;
    }

    private static string FileName(int file) => "file-" + file.ToString("0000", CultureInfo.InvariantCulture) + ".txt";

    private static IEnumerable<string> Lines(int file, string marker)
    {
        for (var line = 0; line < DiffLinesPerFile; line++)
        {
            yield return marker + " line " + line.ToString(CultureInfo.InvariantCulture) + " of file " + file.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition, 600);

}
