using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Forge.App.Git;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>Temp-directory helpers shared by the git panel suites in this folder.</summary>
internal static class GitTestFiles
{
    public static string NewTempDir(string prefix) => TestTempFiles.NewDirectory(prefix);

    /// <summary>Best-effort recursive delete (git object files are read-only on Windows).</summary>
    public static void DeleteTree(string path) => TestTempFiles.DeleteTree(path);
}

/// <summary>Isolated git runner: per-process empty global/system stubs so the developer's config never leaks in.</summary>
internal static class GitTestShell
{
    private static readonly Lazy<(string Global, string System)> Stubs = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-git-support-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var global = Path.Combine(dir, "global.config");
        var system = Path.Combine(dir, "system.config");
        File.WriteAllText(global, string.Empty, new UTF8Encoding(false));
        File.WriteAllText(system, string.Empty, new UTF8Encoding(false));

        // The stubs must outlive every git call of the test process; they are removed when the process ends.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GitTestFiles.DeleteTree(dir);
        return (global, system);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string RunChecked(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
        {
            "-c", "user.name=Forge Test",
            "-c", "user.email=forge@example.com",
            "-c", "core.autocrlf=false",
            "-c", "core.safecrlf=false",
            "-c", "init.defaultBranch=main",
            "-c", "commit.gpgsign=false",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Stubs.Value.Global;
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = Stubs.Value.System;
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr.Result}");
        }

        return stdout.Result;
    }

    public static IReadOnlyList<string> BranchList(string repository) =>
        RunChecked(repository, "branch", "--list", "--format=%(refname:short)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .ToList();
}

/// <summary>A bare <c>origin.git</c> plus clones with pinned local identity.</summary>
internal sealed class GitTestSandbox : IDisposable
{
    private GitTestSandbox(string root, string origin)
    {
        Root = root;
        Origin = origin;
    }

    public string Root { get; }

    public string Origin { get; }

    public static GitTestSandbox Create()
    {
        var root = GitTestFiles.NewTempDir("forge-git-support-");
        var origin = Path.Combine(root, "origin.git");
        GitTestShell.RunChecked(root, "init", "--bare", "-q", "-b", "main", origin);
        return new GitTestSandbox(root, origin);
    }

    public string Clone(string name)
    {
        var path = Path.Combine(Root, name);
        GitTestShell.RunChecked(Root, "clone", "-q", Origin, path);
        GitTestShell.RunChecked(path, "config", "--local", "user.name", "Forge Test");
        GitTestShell.RunChecked(path, "config", "--local", "user.email", "forge@example.com");
        GitTestShell.RunChecked(path, "config", "--local", "core.autocrlf", "false");
        GitTestShell.RunChecked(path, "config", "--local", "commit.gpgsign", "false");
        return path;
    }

    public void CommitFile(string repository, string relativePath, string content, string message)
    {
        var full = Path.Combine(repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        GitTestShell.RunChecked(repository, "add", "-A");
        GitTestShell.RunChecked(repository, "commit", "-q", "-m", message);
    }

    /// <summary>A clone of <c>main</c> with one pushed commit, upstream set - the usual starting point.</summary>
    public string CloneTracked(string name) => CloneWithPushedCommit(name, "file.txt", "v1\n", "initial");

    /// <summary>A clone with one commit of <paramref name="relativePath"/>, pushed to <c>main</c> with the upstream set.</summary>
    public string CloneWithPushedCommit(string name, string relativePath, string content, string message)
    {
        var work = Clone(name);
        CommitFile(work, relativePath, content, message);
        GitTestShell.RunChecked(work, "push", "-q", "-u", "origin", "main");
        return work;
    }

    /// <summary>Commits one file in <paramref name="repository"/> and pushes the checked-out branch to <c>main</c>.</summary>
    public void CommitAndPush(string repository, string relativePath, string content, string message)
    {
        CommitFile(repository, relativePath, content, message);
        GitTestShell.RunChecked(repository, "push", "-q", "origin", "main");
    }

    /// <summary>
    /// Points <c>origin</c> of <paramref name="repository"/> at <paramref name="url"/> and turns off everything that
    /// could make a loopback http call depend on the developer's machine (proxy, credential helpers).
    /// </summary>
    public static void PointOriginAt(string repository, string url)
    {
        GitTestShell.RunChecked(repository, "remote", "set-url", "origin", url);
        GitTestShell.RunChecked(repository, "config", "--local", "http.proxy", string.Empty);
        GitTestShell.RunChecked(repository, "config", "--local", "credential.helper", string.Empty);
    }

    public void Dispose() => GitTestFiles.DeleteTree(Root);
}

/// <summary>
/// A loopback "git remote over http" that answers every request with <c>401 Unauthorized</c>. Git cannot prompt (the panel
/// runs it with prompts disabled), so the call fails as an authentication failure. In hold mode the response is withheld
/// until <see cref="Release"/>, which keeps a panel write (fetch) in flight without sleeping: the test learns that git
/// is mid-call through <see cref="WaitForRequestAsync"/> and ends the call by releasing.
/// </summary>
internal sealed class StubHttpRemote : IAsyncDisposable
{
    private static readonly byte[] UnauthorizedResponse = Encoding.ASCII.GetBytes(
        "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"forge-test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource requestSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource clientGone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task acceptLoop;
    private int requests;
    private int responses;

    public StubHttpRemote(bool hold)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (!hold)
        {
            released.TrySetResult();
        }

        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/repo.git";

    /// <summary>Requests received so far (a git call may issue more than one).</summary>
    public int RequestCount => Volatile.Read(ref requests);

    /// <summary>Responses written so far.</summary>
    public int ResponseCount => Volatile.Read(ref responses);

    /// <summary>Completes when the client side of a held connection went away (the git process was killed).</summary>
    public Task ClientGone => clientGone.Task;

    public Task WaitForRequestAsync() => requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>Ends every held request with the 401 answer.</summary>
    public void Release() => released.TrySetResult();

    /// <summary>A loopback port nothing listens on, for the "connection refused" case.</summary>
    public static string ClosedPortUrl()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}/repo.git";
    }

    public async ValueTask DisposeAsync()
    {
        released.TrySetResult();
        await stop.CancelAsync();
        listener.Stop();
        try
        {
            await acceptLoop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or ObjectDisposedException or SocketException)
        {
        }

        stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, stop.Token);
                    if (read == 0)
                    {
                        clientGone.TrySetResult();
                        return;
                    }

                    seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Interlocked.Increment(ref requests);
                requestSeen.TrySetResult();

                // While held, a read that returns 0 means the client closed the socket (git was killed).
                var gone = Task.Run(
                    async () =>
                    {
                        try
                        {
                            while (await stream.ReadAsync(buffer, stop.Token) > 0)
                            {
                            }

                            clientGone.TrySetResult();
                        }
                        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                        {
                            clientGone.TrySetResult();
                        }
                    });

                await released.Task.WaitAsync(stop.Token);
                await stream.WriteAsync(UnauthorizedResponse, stop.Token);
                await stream.FlushAsync(stop.Token);
                Interlocked.Increment(ref responses);
                client.Client.Shutdown(SocketShutdown.Send);
                await gone.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException or TimeoutException)
            {
            }
        }
    }
}

/// <summary>Builders and bounded waits shared by the panel suites. All waits yield the dispatcher; call them on the UI thread.</summary>
internal static class GitPanelTesting
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    public static GitSyncViewModel NewPanel(
        GitPullStrategy strategy = GitPullStrategy.FastForwardOnly,
        TimeSpan? networkTimeout = null) =>
        new(
            new GitCli(),
            new GitPanelSettings(() => strategy, () => networkTimeout ?? TimeSpan.FromSeconds(30)));

    public static PanelContext Context(string sessionId, string directory) => new(sessionId, directory, null);

    /// <summary>Polls <c>IsBusy == false</c> with a bounded budget; awaiting on the UI thread lets the panel's continuations run.</summary>
    public static async Task WaitIdleAsync(GitSyncViewModel panel)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (panel.IsBusy)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("the git panel stayed busy past the 30 s budget");
            }

            await Task.Delay(Poll);
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("timed out waiting for: " + what);
            }

            await Task.Delay(Poll);
        }
    }

    /// <summary>
    /// Lets the dispatcher run for a fixed window. Used only for NEGATIVE assertions (nothing may happen); the positive
    /// assertions around it never depend on the length of the window.
    /// </summary>
    public static Task QuietWindowAsync(int milliseconds = 600) => Task.Delay(milliseconds);

    /// <summary>The properties whose values describe the last operation result of the attached worktree.</summary>
    public static readonly string[] ResultProperties =
    [
        nameof(GitSyncViewModel.LastResultText),
        nameof(GitSyncViewModel.MessageDetail),
        nameof(GitSyncViewModel.HasMessageDetail),
        nameof(GitSyncViewModel.MessageIsError),
        nameof(GitSyncViewModel.HasResult),
        nameof(GitSyncViewModel.HintText),
        nameof(GitSyncViewModel.HasHint),
    ];
}

/// <summary>
/// An in-memory pull request platform for the screenshot suites: it answers the availability check and the pull request
/// read from what a test sets, records every edit, and throws for everything the screenshot step never calls.
/// </summary>
internal sealed class ScreenshotFakePlatform : ThrowingPrPlatform
{
    private readonly List<(int Number, string? Title, string? Body)> edits = [];

    public override string Name => "FakeHub";

    public PullRequestInfo? Current { get; set; }

    public PrPlatformAvailability Availability { get; set; } = new(PrPlatformState.Ready, "logged in");

    /// <summary>Every accepted edit, in order.</summary>
    public IReadOnlyList<(int Number, string? Title, string? Body)> Edits => edits;

    /// <summary>When true, an accepted edit is not applied (the platform "lost" it).</summary>
    public bool DropEdits { get; set; }

    public override Task<PrPlatformAvailability> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default) =>
        Task.FromResult(Availability);

    public override Task<PrResult<PullRequestInfo>> ViewPullRequestAsync(string workingDirectory, string? selector = null, CancellationToken ct = default) =>
        Task.FromResult(Current is { } current
            ? PrResult<PullRequestInfo>.Ok(current)
            : PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other,
                "no pull requests found",
                reason: PrFailureReason.PullRequestNotFound));

    public override Task<PrResult<PrReference>> EditPullRequestAsync(
        string workingDirectory,
        int number,
        string? title = null,
        string? body = null,
        CancellationToken ct = default)
    {
        lock (edits)
        {
            edits.Add((number, title, body));
        }

        if (!DropEdits && Current is { } current)
        {
            Current = current with { Title = title ?? current.Title, Body = body ?? current.Body };
        }

        return Task.FromResult(PrResult<PrReference>.Ok(new PrReference(number, Current?.Url ?? string.Empty)));
    }

    public override Task<PrResult<IReadOnlyList<PrReviewThread>>> GetReviewThreadsAsync(string workingDirectory, int number, CancellationToken ct = default) =>
        Task.FromResult(PrResult<IReadOnlyList<PrReviewThread>>.Ok([]));
}

/// <summary>
/// A repository that takes its screenshots the way the feature expects: a committed <c>.forge/screenshots.json</c>, a
/// capture script that copies <c>page.png</c> into <c>FORGE_SCREENSHOT_DIR</c>, and an <c>origin</c> that looks like a
/// GitHub repository to the link builder while every fetch and push still lands in the local bare repository (through
/// <c>url.&lt;path&gt;.insteadOf</c>). Shared by the panel and approval suites.
/// </summary>
internal static class ScreenshotTestRig
{
    public const string PublicAddress = "https://github.com/acme/forge.git";

    public const string WindowsCommand = "call .\\capture.cmd";
    public const string UnixCommand = "sh capture.sh";

    private const string CaptureCmd =
        "@echo off\r\n" +
        "copy /Y page.png \"%FORGE_SCREENSHOT_DIR%\\home.png\" >nul || exit /b 1\r\n" +
        "exit /b 0\r\n";

    private const string CaptureSh =
        "#!/bin/sh\n" +
        "cp page.png \"$FORGE_SCREENSHOT_DIR/home.png\" || exit 1\n" +
        "exit 0\n";

    /// <summary>The declaration's command on this platform.</summary>
    public static string Command => OperatingSystem.IsWindows() ? WindowsCommand : UnixCommand;

    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>The pull request the fake platform answers with; base and head are the branch the clone is on.</summary>
    public static PullRequestInfo PullRequest(string branch = "main") =>
        new(
            5,
            "https://example.test/acme/forge/pull/5",
            PrState.Open,
            "Add the screen",
            "## Summary\nIt renders.\n\n## Testing\nRan the UI suite.\n",
            branch,
            branch,
            "0123456789abcdef0123456789abcdef01234567",
            false,
            PrMergeStateStatus.Clean,
            PrReviewDecision.None);

    /// <summary>Commits the page, the capture script and the declaration on the checked-out branch and pushes it.</summary>
    public static void DeclareScreenshots(GitTestSandbox sandbox, string work, string? command = null)
    {
        File.WriteAllBytes(Path.Combine(work, "page.png"), Png());
        File.WriteAllText(Path.Combine(work, "capture.cmd"), CaptureCmd, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(work, "capture.sh"), CaptureSh, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(work, ".forge"));
        var json = "{\"command\": \"" + (command ?? Command).Replace("\\", "\\\\", StringComparison.Ordinal) + "\", \"output\": \"shots\"}";
        File.WriteAllText(Path.Combine(work, ".forge", "screenshots.json"), json, new UTF8Encoding(false));
        GitTestShell.RunChecked(work, "add", "-A");
        GitTestShell.RunChecked(work, "commit", "-q", "-m", "declare screenshots");
        GitTestShell.RunChecked(work, "push", "-q", "origin", "HEAD");
    }

    /// <summary>Makes <c>origin</c> a GitHub address for the link builder while pushes still land in the bare repository.</summary>
    public static void PointOriginAtGitHub(GitTestSandbox sandbox, string work)
    {
        var local = sandbox.Origin.Replace('\\', '/');
        GitTestShell.RunChecked(work, "config", "--local", "remote.origin.url", PublicAddress);
        GitTestShell.RunChecked(work, "config", "--local", "url." + local + ".insteadOf", PublicAddress);
    }
}
