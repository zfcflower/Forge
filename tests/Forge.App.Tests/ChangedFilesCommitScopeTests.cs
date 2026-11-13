using System.Diagnostics;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Tests.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

/// <summary>The commit scope of <see cref="ChangesViewModel"/>: listing, selecting, root commits and session switches.</summary>
[Collection("forge-app-ui")]
public sealed class ChangedFilesCommitScopeTests
{
    private const string SessionId = "commit-scope-session";

    [Fact]
    public async Task Commits_since_the_base_are_listed_newest_first_and_each_one_shows_its_own_diff()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("session");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/session");
        sandbox.CommitFile(work, "first.txt", "first\n", "first commit");
        sandbox.CommitFile(work, "second.txt", "second\n", "second commit");
        sandbox.CommitFile(work, "third.txt", "third\n", "third commit");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();

            Assert.True(changes.HasCommits);
            Assert.False(changes.HasNoCommits);
            Assert.Equal(["third commit", "second commit", "first commit"], changes.Commits.Select(static c => c.Subject).ToArray());
            Assert.All(changes.Commits, static c => Assert.NotNull(c.ParentSha));
            Assert.Null(changes.SelectedCommit);
            Assert.False(changes.IsCommitScope);

            var middle = changes.Commits[1];
            middle.SelectCommand.Execute(null);
            Assert.True(changes.IsCommitScope);
            Assert.Same(middle, changes.SelectedCommit);
            Assert.Equal("second commit", changes.ScopeLabel);
            Assert.True(middle.IsChecked);
            Assert.False(changes.Commits[0].IsChecked);
            await WaitUntilAsync(() => Paths(changes).SequenceEqual(["second.txt"]), "second commit diff");
            Assert.Equal(FileDiffStatus.Added, changes.Files[0].Diff.Status);

            changes.Commits[0].SelectCommand.Execute(null);
            await WaitUntilAsync(() => Paths(changes).SequenceEqual(["third.txt"]), "third commit diff");
            Assert.Equal("third commit", changes.ScopeLabel);
            Assert.False(middle.IsChecked);

            // Leaving the scope keeps the remembered commit but drops its check mark.
            changes.UncommittedCommand.Execute(null);
            Assert.False(changes.IsCommitScope);
            Assert.False(changes.Commits[0].IsChecked);
            Assert.Equal(Loc.Instance[InspectorStrings.ChangesUncommitted], changes.ScopeLabel);
        });
    }

    [Fact]
    public async Task A_modified_file_in_a_commit_diffs_against_its_first_parent()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("session");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/session");
        sandbox.CommitFile(work, "file.txt", "v1\nv2\n", "extend file");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();
            var commit = Assert.Single(changes.Commits);

            commit.SelectCommand.Execute(null);

            await WaitUntilAsync(() => Paths(changes).SequenceEqual(["file.txt"]), "commit diff");
            Assert.Equal(FileDiffStatus.Modified, changes.Files[0].Diff.Status);
            Assert.Single(changes.Files[0].Blocks);
            Assert.Equal(["v2"], changes.Files[0].Blocks[0].Block.NewLines.ToArray());

            // A commit is history: its new side is not the working file, so neither the block nor the file offers revert.
            Assert.False(changes.FilesRevertable);
            Assert.False(changes.Files[0].CanRevert);
            Assert.False(changes.Files[0].RevertFileCommand.CanExecute(null));
            Assert.False(changes.Files[0].Blocks[0].RevertCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task A_root_commit_diffs_against_the_empty_tree()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("history");
        GitTestShell.RunChecked(work, "switch", "--orphan", "unrelated");
        sandbox.CommitFile(work, "unrelated.txt", "unrelated\n", "unrelated root");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            changes.SelectedBranch = Assert.Single(changes.LocalBranches, static branch => branch.Name == "main");
            await changes.LoadAsync(CancellationToken.None);
            await changes.RefreshScopePickerAsync();

            var root = Assert.Single(changes.Commits);
            Assert.Equal("unrelated root", root.Subject);
            Assert.Null(root.ParentSha);

            root.SelectCommand.Execute(null);

            await WaitUntilAsync(() => Paths(changes).SequenceEqual(["unrelated.txt"]), "root commit diff");
            Assert.Equal(FileDiffStatus.Added, changes.Files[0].Diff.Status);
            Assert.Equal("unrelated root", changes.ScopeLabel);
        });
    }

    [Fact]
    public async Task A_branch_without_new_commits_lists_none_and_a_subject_is_trimmed()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("session");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();

            Assert.Empty(changes.Commits);
            Assert.True(changes.HasNoCommits);
            Assert.False(changes.HasCommits);
            Assert.Null(changes.SelectedCommit);
            Assert.Equal(Loc.Instance[InspectorStrings.ChangesUncommitted], changes.ScopeLabel);
        });

        GitTestShell.RunChecked(work, "switch", "-c", "feature/spaced");
        sandbox.CommitFile(work, "spaced.txt", "x\n", "  spaced subject  ");
        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();
            Assert.Equal("spaced subject", Assert.Single(changes.Commits).Subject);
        });
    }

    [Fact]
    public async Task Attaching_another_session_clears_the_commit_choices_and_the_selection()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("session");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/session");
        sandbox.CommitFile(work, "one.txt", "one\n", "one");
        sandbox.CommitFile(work, "two.txt", "two\n", "two");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();
            changes.Commits[0].SelectCommand.Execute(null);
            await WaitUntilAsync(() => Paths(changes).SequenceEqual(["two.txt"]), "commit diff");
            Assert.Equal(2, changes.Commits.Count);

            changes.Attach(new PanelContext("another-session", work, null));

            Assert.Empty(changes.Commits);
            Assert.False(changes.HasCommits);
            Assert.True(changes.HasNoCommits);
            Assert.Null(changes.SelectedCommit);
            // Still the commit scope, but with nothing selected the label falls back to the group title.
            Assert.Equal(Loc.Instance[ChangesPickerStrings.CommitsGroup], changes.ScopeLabel);
            await WaitUntilAsync(() => !changes.IsLoading, "reload after attach");
            Assert.Empty(changes.Files);
        });
    }

    [Fact]
    public async Task Refreshing_the_picker_twice_does_not_duplicate_commits()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("session");
        GitTestShell.RunChecked(work, "switch", "-c", "feature/session");
        sandbox.CommitFile(work, "one.txt", "one\n", "one");

        await RunAsync(async () =>
        {
            using var changes = await OpenAsync(work);
            await changes.RefreshScopePickerAsync();
            await changes.RefreshScopePickerAsync();
            Assert.Single(changes.Commits);

            // A commit made meanwhile shows up on the next refresh, and the selected one stays checked by sha.
            changes.Commits[0].SelectCommand.Execute(null);
            sandbox.CommitFile(work, "later.txt", "later\n", "later");
            await changes.RefreshScopePickerAsync();
            Assert.Equal(["later", "one"], changes.Commits.Select(static c => c.Subject).ToArray());
            Assert.Equal(["one"], changes.Commits.Where(static c => c.IsChecked).Select(static c => c.Subject).ToArray());
        });
    }

    private static string[] Paths(ChangesViewModel changes) => changes.Files.Select(static file => file.Path).ToArray();

    private static async Task<ChangesViewModel> OpenAsync(string work)
    {
        var changes = new ChangesViewModel(new GitCli(), new EmptyCheckpointStore());
        changes.Attach(new PanelContext(SessionId, work, null));
        await changes.LoadAsync(CancellationToken.None);
        return changes;
    }

    private static Task RunAsync(Func<Task> body) => SharedUi.RunAsync(body);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }

}
