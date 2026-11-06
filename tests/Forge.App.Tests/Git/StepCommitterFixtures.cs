using Forge.Abstractions;
using Forge.App.Git;
using Forge.Core.Storage;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// A throwaway git repository in a unique temp directory, hermetic against the developer's global git setup: a local
/// identity, no signing, no CRLF conversion and a hooks directory that is pinned to the repository's own. Everything runs
/// through the real git executable, so the step-commit tests observe real commits, real index state and real hooks.
/// </summary>
internal sealed class StepCommitRepo : IDisposable
{
    private static readonly GitCli GitCliInstance = new() { DefaultTimeout = TimeSpan.FromSeconds(60) };

    private StepCommitRepo(string root, string path)
    {
        Root = root;
        Path = path;
    }

    /// <summary>The unique temp directory that holds the repository and everything a test puts next to it.</summary>
    public string Root { get; }

    /// <summary>The working directory of the repository (branch <c>main</c>).</summary>
    public string Path { get; }

    public string HooksDirectory => System.IO.Path.Combine(Path, ".git", "hooks");

    public static GitCli Cli => GitCliInstance;

    /// <summary>A new temp directory (not yet a repository), for tests that need a path next to a repository.</summary>
    public static string NewRoot()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-step-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Creates a repository named <paramref name="name"/> under <paramref name="root"/> (a fresh temp root when null) with one seed commit.</summary>
    public static async Task<StepCommitRepo> CreateAsync(string? root = null, string name = "repo")
    {
        var ownedRoot = root ?? NewRoot();
        var path = System.IO.Path.Combine(ownedRoot, name);
        Directory.CreateDirectory(path);
        var repo = new StepCommitRepo(ownedRoot, path);
        try
        {
            await repo.GitAsync("init", "-q", "-b", "main");
            await repo.GitAsync("config", "user.name", "Forge Test");
            await repo.GitAsync("config", "user.email", "forge-test@example.invalid");
            await repo.GitAsync("config", "commit.gpgsign", "false");
            await repo.GitAsync("config", "core.autocrlf", "false");
            await repo.GitAsync("config", "core.hooksPath", repo.HooksDirectory.Replace('\\', '/'));
            Directory.CreateDirectory(repo.HooksDirectory);
            repo.WriteFile("seed.txt", "seed\n");
            await repo.GitAsync("add", "-A");
            await repo.GitAsync("commit", "-q", "-m", "seed");
        }
        catch
        {
            repo.Dispose();
            throw;
        }

        return repo;
    }

    /// <summary>Runs git in the repository (or <paramref name="directory"/>) and throws unless it exits with 0; returns trimmed stdout.</summary>
    public async Task<string> GitAsync(params string[] arguments) => await GitInAsync(Path, arguments);

    public static async Task<string> GitInAsync(string directory, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await GitCliInstance.RunAsync(directory, arguments, timeout.Token);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} (in {directory}) exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>Runs git and returns the result whatever the exit code is (a conflicting merge exits 1 on purpose).</summary>
    public async Task<GitCommandResult> TryGitAsync(params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await GitCliInstance.RunAsync(Path, arguments, timeout.Token);
    }

    public Task<string> HeadAsync() => GitAsync("rev-parse", "HEAD");

    public async Task<int> CommitCountAsync() => int.Parse(await GitAsync("rev-list", "--count", "HEAD"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The whole message of the last commit (<c>%B</c>), with LF line endings and no trailing newline.</summary>
    public async Task<string> LastMessageAsync() =>
        (await GitAsync("log", "-1", "--format=%B")).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    /// <summary>The paths the last commit touched, as <c>status\tpath</c> lines in git's order.</summary>
    public async Task<IReadOnlyList<string>> LastCommitChangesAsync() =>
        (await GitAsync("-c", "core.quotepath=false", "show", "--name-status", "--format=", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<string> StatusAsync() => await GitAsync("status", "--porcelain");

    public string FullPath(string relative) => System.IO.Path.Combine(Path, relative);

    public void WriteFile(string relative, string content)
    {
        var full = FullPath(relative);
        var folder = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(full, content);
    }

    /// <summary>Installs an executable hook script (LF line endings, shebang first). Git for Windows runs it through its own sh.</summary>
    public void InstallHook(string hookName, string shellBody)
    {
        Directory.CreateDirectory(HooksDirectory);
        var file = System.IO.Path.Combine(HooksDirectory, hookName);
        File.WriteAllText(file, "#!/bin/sh\n" + shellBody.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void RemoveHook(string hookName)
    {
        var file = System.IO.Path.Combine(HooksDirectory, hookName);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    // ---------------------------------------------------------------- operations in progress

    /// <summary>History for the conflict helpers: <c>a.txt</c> is "base" on main, changed to "topic" on <c>topic</c> and to "main" on main.</summary>
    private async Task PrepareDivergedBranchesAsync()
    {
        WriteFile("a.txt", "base\n");
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", "add a");
        await GitAsync("checkout", "-q", "-b", "topic");
        WriteFile("a.txt", "topic\n");
        await GitAsync("commit", "-q", "-am", "topic change");
        await GitAsync("checkout", "-q", "main");
        WriteFile("a.txt", "main\n");
        await GitAsync("commit", "-q", "-am", "main change");
    }

    /// <summary>Leaves a conflicting <c>git merge topic</c> in progress on main.</summary>
    public async Task StartMergeInProgressAsync()
    {
        await PrepareDivergedBranchesAsync();
        await TryGitAsync("merge", "topic");
        await AssertOperationInProgressAsync("merge");
    }

    /// <summary>Leaves a conflicting <c>git rebase main</c> in progress on topic (HEAD is detached while it runs).</summary>
    public async Task StartRebaseInProgressAsync()
    {
        await PrepareDivergedBranchesAsync();
        await GitAsync("checkout", "-q", "topic");
        await TryGitAsync("rebase", "main");
        await AssertOperationInProgressAsync("rebase");
    }

    /// <summary>Leaves a conflicting <c>git cherry-pick topic</c> in progress on main.</summary>
    public async Task StartCherryPickInProgressAsync()
    {
        await PrepareDivergedBranchesAsync();
        await TryGitAsync("cherry-pick", "topic");
        await AssertOperationInProgressAsync("cherry-pick");
    }

    /// <summary>Leaves a conflicting <c>git revert</c> of an older commit in progress on main.</summary>
    public async Task StartRevertInProgressAsync()
    {
        await PrepareDivergedBranchesAsync();
        WriteFile("a.txt", "main again\n");
        await GitAsync("commit", "-q", "-am", "main second");
        await TryGitAsync("revert", "--no-edit", "HEAD~1");
        await AssertOperationInProgressAsync("revert");
    }

    private async Task AssertOperationInProgressAsync(string expected)
    {
        var state = await Cli.GetConflictStateAsync(Path, CancellationToken.None);
        Assert.True(state.InProgress, $"fixture error: the {expected} should be in progress");
    }

    public void Dispose() => DeleteTree(Root);

    /// <summary>Deletes a directory tree whose git objects are read-only; leftovers are ignored (a temp dir).</summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>
/// A <see cref="StepCommitter"/> over a real repository and a real <see cref="SqliteTodoStore"/>. The store can be
/// wrapped to inject faults or watch calls; items are added through the real store.
/// </summary>
internal sealed class StepCommitHarness : IAsyncDisposable
{
    public const string SessionId = "session-1";

    private int resolveCalls;

    private StepCommitHarness(StepCommitRepo repo, SqliteTodoStore store, ITodoStore seenByCommitter)
    {
        Repo = repo;
        Store = store;
        Committer = new StepCommitter(
            StepCommitRepo.Cli,
            seenByCommitter,
            ResolveAsync,
            () => Enabled);
    }

    private Task<WorktreeResolution> ResolveAsync(string sessionId, CancellationToken ct)
    {
        Interlocked.Increment(ref resolveCalls);
        if (ResolveFailure is { } failure)
        {
            return Task.FromException<WorktreeResolution>(failure);
        }

        if (WorktreeBySession.TryGetValue(sessionId, out var mapped))
        {
            return Task.FromResult(WorktreeResolution.Own(mapped));
        }

        return Task.FromResult(WorktreeResolution.Own(Resolved ?? Repo.Path));
    }

    public StepCommitRepo Repo { get; }

    public SqliteTodoStore Store { get; }

    public StepCommitter Committer { get; }

    /// <summary>The <c>gitStepCommits</c> preference the committer reads on every call.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What the worktree resolver returns; the repository path when null. Set to an empty string for "no worktree".</summary>
    public string? Resolved { get; set; }

    /// <summary>Per-session overrides of the resolver (a second repository for another session); checked before <see cref="Resolved"/>.</summary>
    public Dictionary<string, string?> WorktreeBySession { get; } = new(StringComparer.Ordinal);

    /// <summary>Makes the resolver throw this.</summary>
    public Exception? ResolveFailure { get; set; }

    public int ResolveCalls => Volatile.Read(ref resolveCalls);

    /// <summary>When true the resolver returns null (a session without a worktree of its own).</summary>
    public bool NoWorktree
    {
        set
        {
            if (value)
            {
                WorktreeBySession[SessionId] = null;
            }
            else
            {
                WorktreeBySession.Remove(SessionId);
            }
        }
    }

    public static async Task<StepCommitHarness> CreateAsync(Func<SqliteTodoStore, ITodoStore>? wrap = null)
    {
        var repo = await StepCommitRepo.CreateAsync();
        var data = System.IO.Path.Combine(repo.Root, "data");
        var store = new SqliteTodoStore(data);
        return new StepCommitHarness(repo, store, wrap is null ? store : wrap(store));
    }

    /// <summary>Adds a done item (status Done, as the caller just marked it) with the given step fields.</summary>
    public async Task<TodoItem> AddDoneAsync(
        string title = "Add feature",
        string? commitSubject = null,
        string? summary = null,
        string? verification = null,
        TodoStatus status = TodoStatus.Done)
    {
        var added = await Store.AddAsync(SessionId, title, null, null, CancellationToken.None);
        var step = commitSubject is null && summary is null && verification is null
            ? null
            : new TodoStepFields(commitSubject, summary, verification);
        var updated = await Store.UpdateAsync(added.Id, null, status, null, step, CancellationToken.None);
        return updated ?? throw new InvalidOperationException("the item vanished");
    }

    public async Task<TodoItem> StoredAsync(string id)
    {
        var items = await Store.ListAsync(SessionId, CancellationToken.None);
        return items.Single(item => item.Id == id);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Repo.Dispose();
    }
}

/// <summary>A store that forwards to the real one and lets a test fail or observe single members.</summary>
internal sealed class FaultyTodoStore : ITodoStore
{
    private readonly SqliteTodoStore inner;

    public FaultyTodoStore(SqliteTodoStore inner)
    {
        this.inner = inner;
        inner.Changed += session => Changed?.Invoke(session);
    }

    public event Action<string>? Changed;

    /// <summary>Thrown by <see cref="SetCommitResultAsync"/> when set.</summary>
    public Exception? SetCommitResultFailure { get; set; }

    /// <summary>Thrown by <see cref="ListAsync"/> when set.</summary>
    public Exception? ListFailure { get; set; }

    public int SetCommitResultCalls { get; private set; }

    public Task<IReadOnlyList<TodoItem>> ListAsync(string sessionId, CancellationToken ct) =>
        ListFailure is { } failure ? Task.FromException<IReadOnlyList<TodoItem>>(failure) : inner.ListAsync(sessionId, ct);

    public Task<TodoItem> AddAsync(string sessionId, string title, string? note, string? afterId, CancellationToken ct) =>
        inner.AddAsync(sessionId, title, note, afterId, ct);

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, CancellationToken ct) =>
        inner.UpdateAsync(id, title, status, note, ct);

    public Task<TodoItem?> UpdateAsync(string id, string? title, TodoStatus? status, string? note, TodoStepFields? step, CancellationToken ct) =>
        inner.UpdateAsync(id, title, status, note, step, ct);

    public Task<TodoItem?> SetCommitResultAsync(string id, string? commitId, string? commitError, CancellationToken ct)
    {
        SetCommitResultCalls++;
        return SetCommitResultFailure is { } failure
            ? Task.FromException<TodoItem?>(failure)
            : inner.SetCommitResultAsync(id, commitId, commitError, ct);
    }

    public Task<bool> RemoveAsync(string id, CancellationToken ct) => inner.RemoveAsync(id, ct);

    public Task<bool> MoveAsync(string id, int order, CancellationToken ct) => inner.MoveAsync(id, order, ct);

    public Task<PlanSummary?> GetPlanSummaryAsync(string sessionId, CancellationToken ct) => inner.GetPlanSummaryAsync(sessionId, ct);

    public Task<PlanSummary> SetPlanSummaryAsync(string sessionId, string text, CancellationToken ct) => inner.SetPlanSummaryAsync(sessionId, text, ct);
}
