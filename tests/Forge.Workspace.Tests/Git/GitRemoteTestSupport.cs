using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Isolated git runner for the branch/remote tests. Every invocation pins identity and line-ending
/// behaviour with <c>-c</c> flags (like <see cref="GitFixture"/>) and additionally redirects the
/// global and system config to empty per-process stubs, so a developer's <c>~/.gitconfig</c> or
/// <c>commit.gpgsign=true</c> can never leak into fixtures. All state lives in the child process
/// environment block, so this stays parallel-safe.
/// </summary>
internal static class IsolatedGit
{
    private static readonly Lazy<string> GlobalStub = new(() => WriteStub("global"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> SystemStub = new(() => WriteStub("system"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static string WriteStub(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-git-remote-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".config");
        File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
        return path;
    }

    public static (int ExitCode, string Output, string Error) Run(string directory, params string[] arguments)
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
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = GlobalStub.Value;
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = SystemStub.Value;
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    public static string RunChecked(string directory, params string[] arguments)
    {
        var (exitCode, output, error) = Run(directory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        }

        return output;
    }

    public static string Head(string repository) => RunChecked(repository, "rev-parse", "HEAD").Trim();

    public static string CurrentBranch(string repository) =>
        RunChecked(repository, "branch", "--show-current").Trim();

    public static string? UpstreamOf(string repository, string branch)
    {
        var (exitCode, output, _) = Run(repository, "rev-parse", "--abbrev-ref", "--symbolic-full-name", branch + "@{u}");
        return exitCode == 0 ? output.Trim() : null;
    }

    public static IReadOnlyList<string> BranchList(string repository) =>
        RunChecked(repository, "branch", "--list", "--format=%(refname:short)")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .ToList();

    public static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>
/// A bare <c>origin.git</c> plus clones. The origin starts empty with HEAD on <c>main</c>
/// (<c>init --bare -b main</c>); tests seed it by cloning and pushing.
/// </summary>
internal sealed class RemoteSandbox : IDisposable
{
    private readonly TempDirectory temp = new();

    public RemoteSandbox()
    {
        Origin = Path.Combine(temp.Path, "origin.git");
        IsolatedGit.RunChecked(temp.Path, "init", "--bare", "-q", "-b", "main", Origin);
    }

    public string Origin { get; }

    public string Root => temp.Path;

    /// <summary>Clones origin into the sandbox and pins local identity settings.</summary>
    public string Clone(string name)
    {
        var path = Path.Combine(temp.Path, name);
        IsolatedGit.RunChecked(temp.Path, "clone", "-q", Origin, path);
        IsolatedGit.RunChecked(path, "config", "--local", "user.name", "Forge Test");
        IsolatedGit.RunChecked(path, "config", "--local", "user.email", "forge@example.com");
        IsolatedGit.RunChecked(path, "config", "--local", "core.autocrlf", "false");
        IsolatedGit.RunChecked(path, "config", "--local", "commit.gpgsign", "false");
        return path;
    }

    public string CommitFile(string repository, string relativePath, string content, string message)
    {
        var full = Path.Combine(repository, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        IsolatedGit.RunChecked(repository, "add", "-A");
        IsolatedGit.RunChecked(repository, "commit", "-q", "-m", message);
        return IsolatedGit.Head(repository);
    }

    /// <summary>Clones origin as <paramref name="name"/>, commits <c>base.txt</c> on <c>main</c> and pushes it
    /// upstream, so the clone starts out tracking <c>origin/main</c> with a single commit.</summary>
    public string SeedTracked(string name = "alice")
    {
        var repo = Clone(name);
        CommitFile(repo, "base.txt", "base\n", "seed");
        IsolatedGit.RunChecked(repo, "push", "-q", "-u", "origin", "main");
        return repo;
    }

    public void Dispose() => temp.Dispose();
}

/// <summary>
/// A TCP listener that accepts connections and never answers, so any HTTP fetch against
/// <see cref="Url"/> hangs until the caller's timeout fires. Used to prove timeout handling
/// without depending on the network.
/// </summary>
internal sealed class HangingHttpServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<TcpClient> held = new();

    public HangingHttpServer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(50);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}/repo.git";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                lock (held)
                {
                    held.Add(client);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        listener.Stop();
        lock (held)
        {
            foreach (var client in held)
            {
                client.Dispose();
            }

            held.Clear();
        }

        lifetime.Dispose();
    }
}
