using System.Diagnostics;
using System.Reflection;
using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Minimal end-to-end smoke tests of <see cref="GhCli"/> and <see cref="GitHubPlatform"/> against a scripted stand-in for
/// the gh executable (a .cmd on Windows, a shell script elsewhere) injected through <see cref="GhCli.ExecutablePath"/>.
/// The stand-in records its arguments and a few environment variables, then replies with canned output and exit code.
/// </summary>
public sealed class GhCliSmokeTests
{
    private const string PullRequestJson = """
        {"number":5,"url":"https://github.com/acme/forge/pull/5","state":"OPEN","title":"T","body":"B","headRefName":"feature","baseRefName":"main","headRefOid":"abc","isDraft":false,"mergeStateStatus":"CLEAN","reviewDecision":"APPROVED"}
        """;

    private const string ChecksJson = """
        [{"name":"build","state":"IN_PROGRESS","bucket":"pending","link":"https://github.com/acme/forge/actions/runs/1/job/2","workflow":"CI","startedAt":"2026-05-01T10:00:00Z","completedAt":"0001-01-01T00:00:00Z","description":""},
         {"name":"lint","state":"FAILURE","bucket":"fail","link":"https://github.com/acme/forge/actions/runs/1/job/3","workflow":"CI","startedAt":"2026-05-01T10:00:00Z","completedAt":"2026-05-01T10:01:00Z","description":"boom"}]
        """;

    private const string ThreadsJson = """
        {"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":"c1"},"nodes":[
          {"id":"PRRT_1","isResolved":false,"isOutdated":false,"path":"src/A.cs","line":10,"startLine":null,"originalLine":10,"diffSide":"RIGHT","resolvedBy":null,
           "comments":{"pageInfo":{"hasNextPage":false,"endCursor":"x"},"nodes":[
             {"id":"PRRC_1","databaseId":111,"author":{"login":"reviewer"},"body":"please fix","createdAt":"2026-05-01T10:00:00Z","url":"https://github.com/acme/forge/pull/5#discussion_r111"},
             {"id":"PRRC_2","databaseId":112,"author":null,"body":"ghost reply","createdAt":"2026-05-01T11:00:00Z","url":null}]}}]}}}}}
        """;

    [Fact]
    public async Task Missing_executable_is_reported_as_not_installed_and_never_throws()
    {
        using var temp = new TempDirectory();
        var gh = new GhCli(temp.Combine("no-such-gh-executable"));

        var run = await gh.RunAsync(temp.Path, ["pr", "view"]);
        Assert.True(run.StartFailed);
        Assert.False(run.Success);

        var availability = await gh.GetAvailabilityAsync(temp.Path);
        Assert.Equal(GhAvailability.NotInstalled, availability.State);

        var platform = new GitHubPlatform(gh);
        var view = await platform.ViewPullRequestAsync(temp.Path);
        Assert.False(view.Success);
        Assert.Equal(PrFailureReason.PlatformNotInstalled, view.Reason);
        Assert.Equal(PrPlatformState.NotInstalled, (await platform.GetAvailabilityAsync(temp.Path)).State);
    }

    [Fact]
    public async Task Arguments_pass_verbatim_and_the_environment_is_pinned()
    {
        using var fake = new SmokeFakeGh(stdout: "out");

        var result = await fake.Cli.RunAsync(fake.WorkingDirectory, ["pr", "view", "two words", "--json", "a,b"]);

        Assert.True(result.Success);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Contains("\"two words\"", fake.Calls.Single());

        var environment = fake.EnvironmentDump;
        Assert.Contains("GH_PROMPT_DISABLED=1", environment);
        Assert.Contains("GH_NO_UPDATE_NOTIFIER=1", environment);
        Assert.Contains("GH_PAGER=cat", environment);
        Assert.Contains("NO_COLOR=1", environment);
        Assert.Contains("LC_ALL=C", environment);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", environment);
    }

    [Fact]
    public void Removed_variables_include_everything_GitCli_removes()
    {
        var gitList = (string[])typeof(GitCli)
            .GetField("InheritedRepositoryVariables", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var ghList = (string[])typeof(GhCli)
            .GetField("RemovedVariables", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        Assert.NotEmpty(gitList);
        foreach (var name in gitList)
        {
            Assert.Contains(name, ghList);
        }

        Assert.Contains("GH_REPO", ghList);
    }

    [Fact]
    public async Task Timeout_kills_the_process_and_reports_timed_out()
    {
        using var fake = new SmokeFakeGh(sleep: true);
        var stopwatch = Stopwatch.StartNew();

        var result = await fake.Cli.RunAsync(
            fake.WorkingDirectory,
            ["pr", "view"],
            new GhRunOptions { Timeout = TimeSpan.FromMilliseconds(500) });

        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "the timeout must not wait for the child to finish");
    }

    [Fact]
    public async Task Cancellation_throws_instead_of_returning_a_result()
    {
        using var fake = new SmokeFakeGh(sleep: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fake.Cli.RunAsync(fake.WorkingDirectory, ["pr", "view"], null, cancellation.Token));
    }

    [Fact]
    public async Task Availability_distinguishes_ready_from_not_authenticated()
    {
        using (var ready = new SmokeFakeGh(stdout: "github.com\n  Logged in to github.com account octo (keyring)\n"))
        {
            var result = await ready.Cli.GetAvailabilityAsync(ready.WorkingDirectory);
            Assert.Equal(GhAvailability.Ready, result.State);
            Assert.Contains("auth status", ready.Calls.Single());
        }

        using var anonymous = new SmokeFakeGh(
            exitCode: 1,
            stderr: "You are not logged into any GitHub hosts. To get started with GitHub CLI, please run:  gh auth login\n");
        var unauthenticated = await anonymous.Cli.GetAvailabilityAsync(anonymous.WorkingDirectory);
        Assert.Equal(GhAvailability.NotAuthenticated, unauthenticated.State);
        Assert.Contains("gh auth login", unauthenticated.Detail);
    }

    [Fact]
    public async Task View_returns_the_pull_request()
    {
        using var fake = new SmokeFakeGh(stdout: PullRequestJson);

        var result = await new GitHubPlatform(fake.Cli).ViewPullRequestAsync(fake.WorkingDirectory);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(5, result.Value.Number);
        Assert.Equal(PrMergeStateStatus.Clean, result.Value.MergeStateStatus);
        Assert.Equal(PrReviewDecision.Approved, result.Value.ReviewDecision);
    }

    [Fact]
    public async Task View_of_a_branch_without_a_pull_request_is_a_distinct_failure_not_an_exception()
    {
        using var fake = new SmokeFakeGh(exitCode: 1, stderr: "no pull requests found for branch \"feature\"\n");

        var result = await new GitHubPlatform(fake.Cli).ViewPullRequestAsync(fake.WorkingDirectory);

        Assert.False(result.Success);
        Assert.True(result.PullRequestNotFound);
        Assert.Contains("no pull requests found", result.Status.StandardError);
    }

    [Fact]
    public async Task Selector_that_looks_like_an_option_never_reaches_gh()
    {
        using var fake = new SmokeFakeGh(stdout: PullRequestJson);

        var result = await new GitHubPlatform(fake.Cli).ViewPullRequestAsync(fake.WorkingDirectory, "--web");

        Assert.False(result.Success);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Checks_parse_the_json_even_though_gh_exits_8_while_pending()
    {
        using var fake = new SmokeFakeGh(exitCode: 8, stdout: ChecksJson);

        var result = await new GitHubPlatform(fake.Cli).GetChecksAsync(fake.WorkingDirectory, 5);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Checks.Count);
        Assert.Equal(1, result.Value.Count(PrCheckBucket.Pending));
        Assert.Equal(1, result.Value.Count(PrCheckBucket.Fail));
        Assert.False(result.Value.NoneReported);
    }

    [Fact]
    public async Task Checks_with_failures_exit_1_still_return_the_json()
    {
        using var fake = new SmokeFakeGh(exitCode: 1, stdout: ChecksJson);

        var result = await new GitHubPlatform(fake.Cli).GetChecksAsync(fake.WorkingDirectory, 5);

        Assert.True(result.Success);
        Assert.Equal(2, result.Value!.Checks.Count);
    }

    [Fact]
    public async Task No_checks_reported_is_an_empty_success_but_a_real_error_is_a_failure()
    {
        using (var none = new SmokeFakeGh(exitCode: 1, stderr: "no checks reported on the 'feature' branch\n"))
        {
            var result = await new GitHubPlatform(none.Cli).GetChecksAsync(none.WorkingDirectory, 5);

            Assert.True(result.Success);
            Assert.True(result.Value!.NoneReported);
        }

        using var broken = new SmokeFakeGh(exitCode: 1, stderr: "error connecting to api.github.com\n");
        var failure = await new GitHubPlatform(broken.Cli).GetChecksAsync(broken.WorkingDirectory, 5);

        Assert.False(failure.Success);
        Assert.Equal(GitRemoteFailureKind.Network, failure.Status.Failure);
    }

    [Fact]
    public async Task Create_passes_the_body_through_a_file_and_deletes_it()
    {
        using var fake = new SmokeFakeGh(stdout: "https://github.com/acme/forge/pull/9\n");
        const string body = "Line one\n\nLine three with `ticks` and $dollars and \"quotes\"";

        var result = await new GitHubPlatform(fake.Cli).CreatePullRequestAsync(
            fake.WorkingDirectory, "My title", body, "main", "feature", draft: true);

        Assert.True(result.Success);
        Assert.Equal(9, result.Value!.Number);
        Assert.Equal("https://github.com/acme/forge/pull/9", result.Value.Url);

        var call = fake.Calls.Single();
        Assert.Contains("--body-file", call);
        Assert.Contains("--draft", call);
        Assert.DoesNotContain("Line one", call);
        Assert.Equal(body, fake.BodyCopy);

        var path = ExtractBodyFile(call);
        Assert.False(File.Exists(path), "the temporary body file must be deleted");
    }

    [Fact]
    public async Task Create_failure_still_deletes_the_body_file_and_flags_an_existing_pull_request()
    {
        using var fake = new SmokeFakeGh(
            exitCode: 1,
            stderr: "a pull request for branch \"feature\" into branch \"main\" already exists:\nhttps://github.com/acme/forge/pull/3\n");

        var result = await new GitHubPlatform(fake.Cli).CreatePullRequestAsync(
            fake.WorkingDirectory, "T", "B", "main", "feature");

        Assert.False(result.Success);
        Assert.Equal(PrFailureReason.PullRequestAlreadyExists, result.Reason);
        Assert.False(File.Exists(ExtractBodyFile(fake.Calls.Single())));
    }

    [Fact]
    public async Task Failed_job_log_is_capped_keeping_the_end_with_an_explicit_flag()
    {
        using var fake = new SmokeFakeGh(stdout: new string('a', 20_000) + "THE-END");

        var result = await new GitHubPlatform(fake.Cli) { MaxLogCharacters = 200 }
            .GetFailedJobLogAsync(fake.WorkingDirectory, 1, 2);

        Assert.True(result.Success);
        Assert.True(result.Value!.Truncated);
        Assert.Contains("THE-END", result.Value.Text);
        Assert.True(result.Value.Text.Length < 1000);
    }

    [Fact]
    public async Task Review_threads_are_read_through_graphql_with_variables()
    {
        using var fake = new SmokeFakeGh(stdout: ThreadsJson);
        fake.SetOutputFor("repo", """{"name":"forge","owner":{"login":"acme"},"url":"https://github.com/acme/forge"}""");

        var result = await new GitHubPlatform(fake.Cli).GetReviewThreadsAsync(fake.WorkingDirectory, 5);

        Assert.True(result.Success);
        var thread = Assert.Single(result.Value!);
        Assert.Equal("PRRT_1", thread.Id);
        Assert.False(thread.IsResolved);
        Assert.Equal("src/A.cs", thread.Path);
        Assert.Equal(10, thread.Line);
        Assert.Equal(2, thread.Comments.Count);
        Assert.Equal("reviewer", thread.Comments[0].Author);
        Assert.Null(thread.Comments[1].Author);

        var graphql = fake.Calls.Single(call => call.StartsWith("api graphql", StringComparison.Ordinal));
        Assert.Contains("--hostname github.com", graphql);
        Assert.Contains("owner=acme", graphql);
        Assert.Contains("number=5", graphql);
    }

    [Fact]
    public async Task Graphql_errors_on_a_zero_exit_code_are_reported_as_a_failure()
    {
        using var fake = new SmokeFakeGh(stdout: """{"data":null,"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a node with the global id of 'PRRT_x'."}]}""");
        fake.SetOutputFor("repo", """{"name":"forge","owner":{"login":"acme"},"url":"https://github.com/acme/forge"}""");

        var result = await new GitHubPlatform(fake.Cli).ResolveReviewThreadAsync(fake.WorkingDirectory, "PRRT_x");

        Assert.False(result.Success);
        Assert.Contains("Could not resolve", result.Status.Message);
    }

    [Fact]
    public async Task Resolve_returns_the_new_thread_state()
    {
        using var fake = new SmokeFakeGh(stdout: """{"data":{"resolveReviewThread":{"thread":{"id":"PRRT_1","isResolved":true}}}}""");
        fake.SetOutputFor("repo", """{"name":"forge","owner":{"login":"acme"},"url":"https://github.com/acme/forge"}""");

        var result = await new GitHubPlatform(fake.Cli).ResolveReviewThreadAsync(fake.WorkingDirectory, "PRRT_1");

        Assert.True(result.Success);
        Assert.Equal("PRRT_1", result.Value!.ThreadId);
        Assert.True(result.Value.IsResolved);
    }

    private static string ExtractBodyFile(string call)
    {
        var tokens = call.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(tokens, "--body-file");
        Assert.True(index >= 0 && index + 1 < tokens.Length, "the call must carry --body-file <path>");
        return tokens[index + 1].Trim('"');
    }
}

/// <summary>A scripted stand-in for the gh executable; see <see cref="GhCliSmokeTests"/>.</summary>
internal sealed class SmokeFakeGh : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly TempDirectory root = new();
    private readonly string scriptDirectory;

    public SmokeFakeGh(int exitCode = 0, string stdout = "", string stderr = "", bool sleep = false)
    {
        scriptDirectory = root.Combine("fake");
        Directory.CreateDirectory(scriptDirectory);
        WorkingDirectory = root.Combine("work");
        Directory.CreateDirectory(WorkingDirectory);

        File.WriteAllText(Path.Combine(scriptDirectory, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), Utf8);
        if (stdout.Length > 0)
        {
            File.WriteAllText(Path.Combine(scriptDirectory, "stdout.txt"), stdout, Utf8);
        }

        if (stderr.Length > 0)
        {
            File.WriteAllText(Path.Combine(scriptDirectory, "stderr.txt"), stderr, Utf8);
        }

        if (sleep)
        {
            File.WriteAllText(Path.Combine(scriptDirectory, "sleep.flag"), "x", Utf8);
        }

        Executable = WriteScript();
        Cli = new GhCli(Executable);
    }

    public string Executable { get; }

    public string WorkingDirectory { get; }

    public GhCli Cli { get; }

    /// <summary>One entry per invocation: the arguments as the script saw them.</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            var path = Path.Combine(scriptDirectory, "args.log");
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList()
                : [];
        }
    }

    public string EnvironmentDump
    {
        get
        {
            var path = Path.Combine(scriptDirectory, "env.log");
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
    }

    /// <summary>The body file the script copied when it was passed <c>--body-file</c>.</summary>
    public string? BodyCopy
    {
        get
        {
            var path = Path.Combine(scriptDirectory, "body.copy");
            return File.Exists(path) ? File.ReadAllText(path, Utf8) : null;
        }
    }

    /// <summary>Replaces the canned stdout for calls whose first argument is <paramref name="firstArgument"/>.</summary>
    public void SetOutputFor(string firstArgument, string stdout) =>
        File.WriteAllText(Path.Combine(scriptDirectory, "stdout." + firstArgument + ".txt"), stdout, Utf8);

    public void Dispose() => root.Dispose();

    private string WriteScript()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(scriptDirectory, "gh.cmd");
            const string script = """
                @echo off
                set "DIR=%~dp0"
                echo %* >>"%DIR%args.log"
                set GH_>"%DIR%env.log" 2>nul
                set NO_COLOR>>"%DIR%env.log" 2>nul
                set LC_ALL>>"%DIR%env.log" 2>nul
                set GIT_TERMINAL_PROMPT>>"%DIR%env.log" 2>nul
                set "FIRST=%~1"
                :scan
                if "%~1"=="" goto scanned
                if "%~1"=="--body-file" copy /y "%~2" "%DIR%body.copy" >nul
                shift
                goto scan
                :scanned
                if exist "%DIR%sleep.flag" ping -n 30 127.0.0.1 >nul
                if exist "%DIR%stdout.%FIRST%.txt" (type "%DIR%stdout.%FIRST%.txt") else if exist "%DIR%stdout.txt" type "%DIR%stdout.txt"
                if exist "%DIR%stderr.txt" type "%DIR%stderr.txt" 1>&2
                set /p CODE=<"%DIR%exit.txt"
                exit /b %CODE%
                """;
            File.WriteAllText(path, script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
            return path;
        }
        else
        {
            var path = Path.Combine(scriptDirectory, "gh");
            const string script = """
                #!/bin/sh
                dir=$(dirname "$0")
                printf '%s \n' "$*" >> "$dir/args.log"
                env | grep -E '^(GH_|NO_COLOR|LC_ALL|GIT_TERMINAL_PROMPT)' > "$dir/env.log"
                first="$1"
                while [ $# -gt 0 ]; do
                  if [ "$1" = "--body-file" ]; then cp "$2" "$dir/body.copy"; fi
                  shift
                done
                if [ -f "$dir/sleep.flag" ]; then sleep 30; fi
                if [ -f "$dir/stdout.$first.txt" ]; then cat "$dir/stdout.$first.txt"; elif [ -f "$dir/stdout.txt" ]; then cat "$dir/stdout.txt"; fi
                if [ -f "$dir/stderr.txt" ]; then cat "$dir/stderr.txt" >&2; fi
                exit "$(cat "$dir/exit.txt")"
                """;
            File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n", Utf8);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            return path;
        }
    }
}
