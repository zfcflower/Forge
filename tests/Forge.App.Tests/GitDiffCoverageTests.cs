using System.Diagnostics;
using Forge.Abstractions;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests;

public sealed class GitDiffCoverageTests : IDisposable
{
    private const string Session = "s1";
    private const int MaxChars = 24_000;
    private readonly string repo = Path.Combine(Path.GetTempPath(), "forge-diffcap-" + Guid.NewGuid().ToString("n"));
    private readonly GitReviewLedger ledger = new();
    private readonly GitTools tools;

    public GitDiffCoverageTests()
    {
        tools = new GitTools(new GitCli(), new GitToolSettings(), ledger);
        Directory.CreateDirectory(repo);
        Run("init", "-q", "-b", "main");
        Run("config", "user.email", "t@t");
        Run("config", "user.name", "t");
        Run("config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "base.txt"), "base\n");
        Run("add", "-A");
        Run("commit", "-q", "-m", "init");
        Run("checkout", "-q", "-b", "feature");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(repo, true);
        }
        catch (IOException)
        {
        }
    }

    private void Run(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private string Head()
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true };
        info.ArgumentList.Add("rev-parse");
        info.ArgumentList.Add("HEAD");
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text;
    }

    private void Commit(string name, int lines)
    {
        File.WriteAllText(Path.Combine(repo, name), string.Concat(Enumerable.Range(0, lines).Select(i => $"line {i} of {name} xxxxxxxxxxxxxxxxxxxxxxxx\n")));
        Run("add", "-A");
        Run("commit", "-q", "-m", name);
    }

    private async Task<string> Diff(string json = "{}")
    {
        var result = await tools.Diff.InvokeAsync(new ToolCall("c", GitTools.DiffName, json), new ToolContext(Session, repo), CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    [Fact]
    public async Task A_small_diff_is_shown_whole_and_covers_every_file()
    {
        Commit("a.txt", 5);
        Commit("b.txt", 5);

        var text = await Diff();

        Assert.Contains("a.txt", text);
        Assert.Contains("b.txt", text);
        Assert.DoesNotContain("not shown yet", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ledger.Unread(Session, repo, Head())!);
    }

    [Fact]
    public async Task An_oversized_diff_names_the_files_it_left_out_and_paths_reads_complete_the_coverage()
    {
        Commit("a.txt", 300);
        Commit("b.txt", 300);
        Commit("c.txt", 300);

        var text = await Diff();

        Assert.True(text.Length <= MaxChars, $"length {text.Length}");
        Assert.Contains("files shown", text);
        var unread = ledger.Unread(Session, repo, Head())!;
        Assert.NotEmpty(unread);
        foreach (var file in unread)
        {
            Assert.Contains(file, text[text.IndexOf("files shown", StringComparison.Ordinal)..]);
        }

        foreach (var file in unread.ToArray())
        {
            await Diff($$"""{"paths":["{{file}}"]}""");
        }

        Assert.Empty(ledger.Unread(Session, repo, Head())!);
    }

    [Fact]
    public async Task A_single_file_larger_than_the_cap_is_still_shown_whole()
    {
        Commit("big.txt", 2_000);

        var text = await Diff();

        Assert.True(text.Length > MaxChars);
        Assert.Contains("line 1999 of big.txt", text);
        // Longer than the spill threshold: the model may only get a preview, so the gate neither counts it covered nor waits for it.
        Assert.Contains("read_artifact", text);
        Assert.Empty(ledger.Unread(Session, repo, Head())!);
    }

    [Fact]
    public async Task A_new_commit_resets_the_coverage()
    {
        Commit("a.txt", 5);
        await Diff();
        var before = Head();
        Assert.Empty(ledger.Unread(Session, repo, before)!);

        Commit("b.txt", 5);

        Assert.Null(ledger.Unread(Session, repo, Head()));
        Assert.NotEqual(before, Head());
    }

    [Fact]
    public async Task A_paths_read_alone_does_not_establish_the_full_list_and_stat_only_records_nothing()
    {
        Commit("a.txt", 5);
        Commit("b.txt", 5);

        await Diff("""{"paths":["a.txt"]}""");
        await Diff("""{"stat_only":true}""");

        Assert.Null(ledger.LastReviewed(Session, repo));
        Assert.Null(ledger.Unread(Session, repo, Head()));
    }

    [Fact]
    public async Task A_file_changed_in_a_commit_and_in_the_worktree_is_one_unit()
    {
        Commit("a.txt", 5);
        File.AppendAllText(Path.Combine(repo, "a.txt"), "extra\n");

        var text = await Diff();

        Assert.Contains("# committed", text);
        Assert.Contains("# uncommitted", text);
        Assert.Empty(ledger.Unread(Session, repo, Head())!);
    }

    [Fact]
    public async Task A_rename_shows_its_old_and_new_path()
    {
        Run("checkout", "-q", "main");
        Commit("old.txt", 30);
        Run("checkout", "-q", "feature");
        Run("merge", "-q", "main");
        Run("mv", "old.txt", "new.txt");
        Run("commit", "-q", "-m", "rename");

        var text = await Diff();

        Assert.Contains("rename from old.txt", text);
        Assert.Contains("rename to new.txt", text);
    }

    [Fact]
    public void A_slow_record_for_an_older_head_does_not_wipe_a_newer_one()
    {
        var older = ledger.BeginRead();
        var newer = ledger.BeginRead();
        ledger.RecordFullRead(Session, repo, "H2", newer, ["a"], ["a"]);

        ledger.RecordShown(Session, repo, "H1", older, ["b"]);

        Assert.Empty(ledger.Unread(Session, repo, "H2")!);
        Assert.Null(ledger.Unread(Session, repo, "H1"));
    }
}
