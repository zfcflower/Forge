using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The two paginated reads of <see cref="GitHubPlatform"/> against a scripted gh: <c>reviewThreads</c> (100 threads a page)
/// and the extra comment pages of one thread (100 comments a page). The stand-in answers the n-th invocation with
/// <c>resp.n.txt</c>, so a test writes the exact sequence of pages it expects. No real GitHub is contacted.
/// </summary>
public sealed class GitHubPlatformPaginationTests
{
    private const string RepositoryJson =
        """{"name":"forge","owner":{"login":"acme"},"url":"https://github.com/acme/forge"}""";

    private static string ThreadsPage(string threads, bool hasNextPage, string? endCursor) =>
        "{\"data\":{\"repository\":{\"pullRequest\":{\"reviewThreads\":{\"pageInfo\":{\"hasNextPage\":" +
        (hasNextPage ? "true" : "false") + ",\"endCursor\":" + Quote(endCursor) + "},\"nodes\":[" + threads + "]}}}}}";

    private static string Thread(string id, string comments, bool moreComments = false, string? commentsEndCursor = null) =>
        "{\"id\":\"" + id + "\",\"isResolved\":false,\"isOutdated\":false,\"path\":\"src/" + id +
        ".cs\",\"line\":10,\"startLine\":null,\"originalLine\":10,\"diffSide\":\"RIGHT\",\"resolvedBy\":null," +
        "\"comments\":{\"pageInfo\":{\"hasNextPage\":" + (moreComments ? "true" : "false") + ",\"endCursor\":" +
        Quote(commentsEndCursor) + "},\"nodes\":[" + comments + "]}}";

    private static string CommentsPage(string comments, bool hasNextPage, string? endCursor) =>
        "{\"data\":{\"node\":{\"comments\":{\"pageInfo\":{\"hasNextPage\":" + (hasNextPage ? "true" : "false") +
        ",\"endCursor\":" + Quote(endCursor) + "},\"nodes\":[" + comments + "]}}}}";

    private static string Comment(string id, string body) =>
        "{\"id\":\"" + id + "\",\"databaseId\":null,\"author\":{\"login\":\"reviewer\"},\"body\":\"" + body +
        "\",\"createdAt\":\"2026-05-01T10:00:00Z\",\"url\":null}";

    private static string Quote(string? value) => value is null ? "null" : "\"" + value + "\"";

    [Fact]
    public async Task Every_thread_and_every_comment_of_every_page_is_returned()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);

        // Page 1: thread A needs a second comment page, thread B is complete.
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first"), moreComments: true, commentsEndCursor: "x1") + "," +
            Thread("B", Comment("B1", "only")), hasNextPage: true, endCursor: "c1"));
        fake.Respond(3, CommentsPage(Comment("A2", "second"), hasNextPage: true, endCursor: "x2"));
        fake.Respond(4, CommentsPage(Comment("A3", "third"), hasNextPage: false, endCursor: null));

        // Page 2 of the thread listing: thread C, and nothing after it.
        fake.Respond(5, ThreadsPage(Thread("C", Comment("C1", "last")), hasNextPage: false, endCursor: null));

        var threads = await fake.Platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.True(threads.Success, threads.Status.Message);
        Assert.Equal(["A", "B", "C"], threads.Value!.Select(thread => thread.Id).ToArray());
        Assert.Equal(["A1", "A2", "A3"], threads.Value![0].Comments.Select(comment => comment.Id).ToArray());
        Assert.Equal(["B1"], threads.Value![1].Comments.Select(comment => comment.Id).ToArray());
        Assert.Equal(["C1"], threads.Value![2].Comments.Select(comment => comment.Id).ToArray());
        Assert.Equal(5, fake.Calls);
    }

    [Fact]
    public async Task A_page_that_promises_more_but_gives_no_cursor_is_refused_instead_of_returning_part_of_the_list()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first")), hasNextPage: true, endCursor: null));

        var threads = await fake.Platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.False(threads.Success);
        Assert.Null(threads.Value);
        Assert.Contains("no usable cursor", threads.Status.Message, StringComparison.Ordinal);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task A_cursor_that_does_not_advance_is_refused_instead_of_looping_forever()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first")), hasNextPage: true, endCursor: "c1"));
        fake.Respond(3, ThreadsPage(Thread("A", Comment("A1", "first")), hasNextPage: true, endCursor: "c1"));

        var threads = await fake.Platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.False(threads.Success);
        Assert.Null(threads.Value);
        Assert.Contains("no usable cursor", threads.Status.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task More_thread_pages_than_the_bound_are_refused_instead_of_returning_a_partial_list()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first")), hasNextPage: true, endCursor: "c1"));
        fake.Respond(3, ThreadsPage(Thread("B", Comment("B1", "second")), hasNextPage: true, endCursor: "c2"));
        var platform = fake.CreatePlatform(maxThreadPages: 2);

        var threads = await platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.False(threads.Success);
        Assert.Null(threads.Value);
        Assert.Contains("refusing to return a partial list", threads.Status.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task More_comment_pages_than_the_bound_are_refused_instead_of_returning_a_partial_thread()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first"), moreComments: true, commentsEndCursor: "x1"), hasNextPage: false, endCursor: null));
        fake.Respond(3, CommentsPage(Comment("A2", "second"), hasNextPage: true, endCursor: "x2"));
        var platform = fake.CreatePlatform(maxCommentPages: 1);

        var threads = await platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.False(threads.Success);
        Assert.Null(threads.Value);
        Assert.Contains("refusing to return a partial thread", threads.Status.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.Calls);
    }

    [Fact]
    public async Task A_comment_page_that_promises_more_but_gives_no_cursor_is_refused()
    {
        using var fake = new ScriptedGh();
        fake.Respond(1, RepositoryJson);
        fake.Respond(2, ThreadsPage(Thread("A", Comment("A1", "first"), moreComments: true, commentsEndCursor: "x1"), hasNextPage: false, endCursor: null));
        fake.Respond(3, CommentsPage(Comment("A2", "second"), hasNextPage: true, endCursor: null));

        var threads = await fake.Platform.GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.False(threads.Success);
        Assert.Null(threads.Value);
        Assert.Contains("no usable cursor", threads.Status.Message, StringComparison.Ordinal);
        Assert.Equal(3, fake.Calls);
    }
}

/// <summary>
/// A scripted stand-in for the gh executable (a .cmd on Windows, a shell script elsewhere) that answers the n-th invocation
/// with <c>resp.n.txt</c> and records how often it ran. See <see cref="GitHubPlatformPaginationTests"/>.
/// </summary>
internal sealed class ScriptedGh : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TempDirectory root = new();
    private readonly string scriptDirectory;
    private readonly string executable;

    public ScriptedGh()
    {
        scriptDirectory = root.Combine("fake");
        Directory.CreateDirectory(scriptDirectory);
        WorkingDirectory = root.Combine("work");
        Directory.CreateDirectory(WorkingDirectory);
        executable = WriteScript();
    }

    public string WorkingDirectory { get; }

    public GitHubPlatform Platform => CreatePlatform();

    /// <summary>The same platform with different pagination bounds, so the refusals can be reached in a few pages.</summary>
    public GitHubPlatform CreatePlatform(int maxThreadPages = 100, int maxCommentPages = 50) =>
        new(new GhCli(executable)) { MaxThreadPages = maxThreadPages, MaxCommentPages = maxCommentPages };

    /// <summary>How many times the stand-in ran, which is how many platform calls were made.</summary>
    public int Calls => File.Exists(Path.Combine(scriptDirectory, "count.txt"))
        ? int.Parse(File.ReadAllText(Path.Combine(scriptDirectory, "count.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture)
        : 0;

    /// <summary>The stdout of the <paramref name="call"/>-th invocation (1-based).</summary>
    public void Respond(int call, string stdout, int exitCode = 0, string stderr = "")
    {
        File.WriteAllText(Path.Combine(scriptDirectory, "resp." + call.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), stdout, Utf8);
        File.WriteAllText(Path.Combine(scriptDirectory, "exit." + call.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), Utf8);
        if (stderr.Length > 0)
        {
            File.WriteAllText(Path.Combine(scriptDirectory, "stderr." + call.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"), stderr, Utf8);
        }
    }

    public void Dispose() => root.Dispose();

    private string WriteScript()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(scriptDirectory, "gh.cmd");
            const string script = """
                @echo off
                set "DIR=%~dp0"
                set "N=0"
                if exist "%DIR%count.txt" set /p N=<"%DIR%count.txt"
                set /a N=N+1
                >"%DIR%count.txt" echo %N%
                if exist "%DIR%resp.%N%.txt" type "%DIR%resp.%N%.txt"
                if exist "%DIR%stderr.%N%.txt" type "%DIR%stderr.%N%.txt" 1>&2
                set "CODE=0"
                if exist "%DIR%exit.%N%.txt" set /p CODE=<"%DIR%exit.%N%.txt"
                exit /b %CODE%
                """;
            File.WriteAllText(path, script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
            return path;
        }

        var shell = Path.Combine(scriptDirectory, "gh");
        const string content = """
            #!/bin/sh
            dir=$(dirname "$0")
            n=0
            [ -f "$dir/count.txt" ] && n=$(cat "$dir/count.txt")
            n=$((n+1))
            printf '%s' "$n" > "$dir/count.txt"
            [ -f "$dir/resp.$n.txt" ] && cat "$dir/resp.$n.txt"
            [ -f "$dir/stderr.$n.txt" ] && cat "$dir/stderr.$n.txt" >&2
            code=0
            [ -f "$dir/exit.$n.txt" ] && code=$(cat "$dir/exit.$n.txt")
            exit "$code"
            """;
        File.WriteAllText(shell, content.ReplaceLineEndings("\n") + "\n", Utf8);
        File.SetUnixFileMode(
            shell,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return shell;
    }
}
